using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Http;

/// <summary>What a LinkedIn post carries, whoever its author is.</summary>
internal sealed record LinkedInPostContent(
    string Text,
    string? LinkUrl,
    string? LinkTitle,
    string? LinkDescription,
    string? ImageUrl,
    string? ImageAltText,
    string? FirstComment);

/// <summary>A published post's URN (empty when LinkedIn didn't return it) and any warning, or the failure that stopped it.</summary>
internal sealed record LinkedInPublishResult(string PostUrn, string? Warning, ActionResult? Failure);

/// <summary>
/// Publishes a post through LinkedIn's versioned Posts API (<c>POST /rest/posts</c>) for an author URN, a company page
/// (<c>urn:li:organization:{id}</c>) or a member (<c>urn:li:person:{id}</c>), with an article link or one image, then an optional first
/// comment as the same author. Shared by the company page and profile actions so the two post the same way.
/// </summary>
/// <remarks>
/// Posts API: https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api (w_organization_social for a page,
/// w_member_social for a member). Images API: https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/images-api
/// (the owner is the author). LinkedIn has no idempotency key, so the create request is never retried once it may have reached LinkedIn
/// (see <see cref="PostOutcome"/>).
/// </remarks>
internal sealed class LinkedInPostPublisher
{
    private const string Network = "LinkedIn";
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxImagePolls = 10;

    private readonly ImageFetcher _imageFetcher;

    public LinkedInPostPublisher(ImageFetcher imageFetcher) => _imageFetcher = imageFetcher;

    /// <summary>
    /// Checks what both actions require of a post: text, a link or an image but not both, and a title with a link. Null when it is fine.
    /// </summary>
    public static ActionResult? Validate(LinkedInPostContent content)
    {
        if (string.IsNullOrWhiteSpace(content.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        var hasLink = !string.IsNullOrWhiteSpace(content.LinkUrl);
        if (hasLink && !string.IsNullOrWhiteSpace(content.ImageUrl))
        {
            return ActionResult.Failed(new ArgumentException("A LinkedIn post can have a link or an image, not both."), StepRunErrorCategory.Validation);
        }

        if (hasLink && string.IsNullOrWhiteSpace(content.LinkTitle))
        {
            return ActionResult.Failed(new ArgumentException("A link needs a title: LinkedIn doesn't fetch it from the page."), StepRunErrorCategory.Validation);
        }

        return null;
    }

    /// <summary>
    /// Publishes the post as <paramref name="author"/>. <paramref name="imageStatusMayBeForbidden"/> is for a member token: LinkedIn says
    /// "tokens with only w_member_social permissions would be unable to perform a GET call for rest/images" (Images API, Permissions), so
    /// when the status check is refused the image is used after one more wait instead of failing the post.
    /// </summary>
    public async Task<LinkedInPublishResult> PublishAsync(
        ActionContext? context,
        HttpClient client,
        string token,
        string author,
        LinkedInPostContent content,
        TimeSpan imagePollDelay,
        bool imageStatusMayBeForbidden,
        CancellationToken cancellationToken)
    {
        var post = new JsonObject
        {
            ["author"] = author,
            ["commentary"] = LittleText.Escape(content.Text),
            ["visibility"] = "PUBLIC",
            ["distribution"] = new JsonObject
            {
                ["feedDistribution"] = "MAIN_FEED",
                ["targetEntities"] = new JsonArray(),
                ["thirdPartyDistributionChannels"] = new JsonArray(),
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReshareDisabledByAuthor"] = false,
        };

        if (!string.IsNullOrWhiteSpace(content.LinkUrl))
        {
            var article = new JsonObject { ["source"] = content.LinkUrl, ["title"] = content.LinkTitle };
            if (!string.IsNullOrWhiteSpace(content.LinkDescription))
            {
                article["description"] = content.LinkDescription;
            }

            post["content"] = new JsonObject { ["article"] = article };
        }

        if (!string.IsNullOrWhiteSpace(content.ImageUrl))
        {
            var (imageUrn, failure) = await UploadImageAsync(
                context, client, token, author, content.ImageUrl, imagePollDelay, imageStatusMayBeForbidden, cancellationToken);
            if (failure is not null)
            {
                return new LinkedInPublishResult(string.Empty, null, failure);
            }

            var media = new JsonObject { ["id"] = imageUrn };
            if (!string.IsNullOrWhiteSpace(content.ImageAltText))
            {
                media["altText"] = content.ImageAltText;
            }

            post["content"] = new JsonObject { ["media"] = media };
        }

        string postUrn;
        try
        {
            using var response = await client.PostAsJsonAsync("posts", post, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new LinkedInPublishResult(string.Empty, null, PostOutcome.FromResponse(context, Network, response.StatusCode, body, idempotent: false));
            }

            // 201 with an empty body; the new post's URN is in a header.
            postUrn = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.First() : string.Empty;
        }
        catch (Exception ex)
        {
            return new LinkedInPublishResult(string.Empty, null, PostOutcome.FromException(Network, ex, idempotent: false));
        }

        // The post is live from here on: a failed comment is a warning, never a failure (a retry would post twice).
        string? warning = null;
        if (!string.IsNullOrWhiteSpace(content.FirstComment))
        {
            warning = postUrn.Length == 0
                ? "The post was published but LinkedIn did not return its id, so the first comment was not added."
                : await AddFirstCommentAsync(context, client, postUrn, author, content.FirstComment, cancellationToken);
        }

        return new LinkedInPublishResult(postUrn, warning, null);
    }

    /// <summary>The post's public link, or empty without a URN.</summary>
    public static string PostUrl(string postUrn) => postUrn.Length > 0 ? $"https://www.linkedin.com/feed/update/{postUrn}/" : string.Empty;

    /// <summary>Comments on the new post as its author. Returns a warning when that fails, or null on success.</summary>
    private static async Task<string?> AddFirstCommentAsync(
        ActionContext? context, HttpClient client, string postUrn, string actor, string text, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"socialActions/{Uri.EscapeDataString(postUrn)}/comments",
                new JsonObject { ["actor"] = actor, ["object"] = postUrn, ["message"] = new JsonObject { ["text"] = text } },
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var outcome = ThreadParts.IsAmbiguous(response.StatusCode) ? "may not have been added" : "could not be added";
            NetworkError.Log(context, Network, "first comment", response.StatusCode, body);
            return $"The post was published but the first comment {outcome}: {NetworkError.Plain(Network, "first comment", response.StatusCode, body)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return $"The post was published but the first comment may not have been added: {ex.Message}";
        }
    }

    /// <summary>
    /// Registers an image with LinkedIn, uploads it, and waits until LinkedIn has processed it.
    /// </summary>
    private async Task<(string? ImageUrn, ActionResult? Failure)> UploadImageAsync(
        ActionContext? context, HttpClient client, string token, string owner, string imageUrl, TimeSpan pollDelay, bool statusMayBeForbidden, CancellationToken cancellationToken)
    {
        var (image, imageError) = await _imageFetcher.FetchAsync(imageUrl, MaxImageBytes, cancellationToken);
        if (image is null)
        {
            return (null, ActionResult.Failed(new ArgumentException(imageError), StepRunErrorCategory.Validation));
        }

        using var init = await client.PostAsJsonAsync(
            "images?action=initializeUpload",
            new { initializeUploadRequest = new { owner } },
            cancellationToken);
        var initBody = await init.Content.ReadAsStringAsync(cancellationToken);
        if (!init.IsSuccessStatusCode)
        {
            return (null, PostOutcome.FromPreparationResponse(context, Network, "image registration", init.StatusCode, initBody));
        }

        string uploadUrl, imageUrn;
        using (var doc = JsonDocument.Parse(initBody))
        {
            var value = doc.RootElement.GetProperty("value");
            uploadUrl = value.GetProperty("uploadUrl").GetString()!;
            imageUrn = value.GetProperty("image").GetString()!;
        }

        using (var upload = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(image.Bytes) })
        {
            upload.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var uploaded = await client.SendAsync(upload, cancellationToken);
            if (!uploaded.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context,
                    Network, "image upload", uploaded.StatusCode, await uploaded.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        // The image moves WAITING_UPLOAD → PROCESSING → AVAILABLE; a post can only use it once AVAILABLE.
        for (var attempt = 0; attempt < MaxImagePolls; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(pollDelay, cancellationToken);
            }

            using var check = await client.GetAsync($"images/{Uri.EscapeDataString(imageUrn)}", cancellationToken);
            var body = await check.Content.ReadAsStringAsync(cancellationToken);
            if (statusMayBeForbidden && check.StatusCode == HttpStatusCode.Forbidden)
            {
                // A member token can't read the image's status: give LinkedIn one more wait, then use it.
                await Task.Delay(pollDelay, cancellationToken);
                return (imageUrn, null);
            }

            if (!check.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "image status", check.StatusCode, body));
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "AVAILABLE")
            {
                return (imageUrn, null);
            }

            if (status is "PROCESSING_FAILED")
            {
                return (null, ActionResult.Failed(
                    new InvalidOperationException("LinkedIn could not process the image."), StepRunErrorCategory.Validation));
            }
        }

        return (null, ActionResult.Failed(new TimeoutException("LinkedIn is still processing the image."), StepRunErrorCategory.Timeout));
    }
}
