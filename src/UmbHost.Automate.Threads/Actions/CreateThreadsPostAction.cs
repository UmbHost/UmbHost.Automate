using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;
using UmbHost.Automate.Threads.Connection;

namespace UmbHost.Automate.Threads.Actions;

/// <summary>
/// Publishes a post to Threads, optionally with one image, and any thread parts as replies, each to the part before.
/// </summary>
/// <remarks>
/// <para>
/// Each part is a media container (<c>POST /v1.0/{user-id}/threads</c>), checked until Threads has processed it
/// (<c>GET /v1.0/{container-id}?fields=status,error_message</c>), then published (<c>POST /v1.0/{user-id}/threads_publish</c>); see
/// https://developers.facebook.com/docs/threads/posts and https://developers.facebook.com/docs/threads/reference/publishing. Threads
/// fetches the image from its public URL. Replies set <c>reply_to_id</c>
/// (https://developers.facebook.com/docs/threads/retrieve-and-manage-replies/create-replies).
/// </para>
/// <para>
/// Nothing is live until a container is published, so a failure before that is safe to retry; Threads has no idempotency key, so a
/// publish request with no clear answer is never retried (see <see cref="PostOutcome"/>), and a failed later part ends the thread with
/// the <see cref="ThreadParts"/> tail.
/// </para>
/// </remarks>
[Action("threads.createPost", "Create Threads Post",
    Description = "Publishes a post to Threads, with an optional image and further parts as a thread.",
    Group = "Social",
    Icon = "icon-umbsocial-threads",
    ConnectionTypeAlias = "threads")]
public sealed class CreateThreadsPostAction : ActionBase<CreateThreadsPostSettings, CreateThreadsPostOutput>
{
    private const string Network = "Threads";
    private const string Graph = "https://graph.threads.com/v1.0/";
    private const int MaxAltTextLength = 1000;
    private const int MaxStatusChecks = 10;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ThreadsTokens _tokens;

    public CreateThreadsPostAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, ThreadsTokens tokens)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
    }

    /// <summary>The wait between checks that Threads has processed a container (Meta suggests allowing about 30 seconds in all).</summary>
    internal TimeSpan StatusPollDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateThreadsPostSettings>();

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        if (settings.ImageAltText?.Length > MaxAltTextLength)
        {
            return ActionResult.Failed(new ArgumentException($"Alt text is limited to {MaxAltTextLength} characters."), StepRunErrorCategory.Validation);
        }

        var (threadParts, threadError) = ThreadParts.Parse(settings.ThreadParts);
        if (threadError is not null)
        {
            return ActionResult.Failed(new ArgumentException(threadError), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<ThreadsConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A Threads connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _tokens.GetAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The Threads token has expired or was revoked. Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var me = await client.GetAsync(Graph + "me?fields=id,username", cancellationToken);
        var meBody = await me.Content.ReadAsStringAsync(cancellationToken);
        if (!me.IsSuccessStatusCode)
        {
            return PostOutcome.FromPreparationResponse(Network, "account lookup", me.StatusCode, meBody);
        }

        string userId;
        using (var doc = JsonDocument.Parse(meBody))
        {
            userId = doc.RootElement.GetProperty("id").GetString()!;
        }

        var parts = new List<string> { settings.Text };
        parts.AddRange(threadParts);
        var urls = new List<string>();
        string? firstId = null;
        string? previousId = null;
        for (var i = 0; i < parts.Count; i++)
        {
            var image = i == 0 && !string.IsNullOrWhiteSpace(settings.ImageUrl);
            var fields = new Dictionary<string, string> { ["media_type"] = image ? "IMAGE" : "TEXT", ["text"] = parts[i] };
            if (image)
            {
                fields["image_url"] = settings.ImageUrl!;
                if (!string.IsNullOrWhiteSpace(settings.ImageAltText))
                {
                    fields["alt_text"] = settings.ImageAltText;
                }
            }

            if (previousId is not null)
            {
                fields["reply_to_id"] = previousId;
            }

            var step = await PostPartAsync(client, userId, fields, cancellationToken);
            if (step.MediaId is null)
            {
                return i == 0
                    ? step.Failure!
                    : ThreadParts.PartFailed(Network, i + 1, parts.Count, urls, step.Detail!, step.Unknown);
            }

            firstId ??= step.MediaId;
            previousId = step.MediaId;
            urls.Add(await PermalinkAsync(client, step.MediaId, cancellationToken));
        }

        return Success(new CreateThreadsPostOutput { PostId = firstId!, PostUrl = urls[0], ThreadUrls = urls });
    }

    /// <summary>Creates, waits for and publishes one container. On failure: the step failure (first part) and a plain detail (later parts).</summary>
    private async Task<(string? MediaId, ActionResult? Failure, string? Detail, bool Unknown)> PostPartAsync(
        HttpClient client, string userId, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var created = await client.PostAsync($"{Graph}{userId}/threads", new FormUrlEncodedContent(fields), cancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
        if (!created.IsSuccessStatusCode)
        {
            // Nothing is live until the container is published, so this is never an unknown outcome.
            return (null, PostOutcome.FromPreparationResponse(Network, "post preparation", created.StatusCode, createdBody),
                $"{Network} returned {(int)created.StatusCode}: {createdBody}", false);
        }

        string containerId;
        using (var doc = JsonDocument.Parse(createdBody))
        {
            containerId = doc.RootElement.GetProperty("id").GetString()!;
        }

        var notReady = await WaitUntilProcessedAsync(client, containerId, cancellationToken);
        if (notReady is not null)
        {
            return (null, notReady.Value.Failure, notReady.Value.Detail, false);
        }

        string body;
        try
        {
            using var published = await client.PostAsync(
                $"{Graph}{userId}/threads_publish",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = containerId }),
                cancellationToken);
            body = await published.Content.ReadAsStringAsync(cancellationToken);
            if (!published.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromResponse(Network, published.StatusCode, body, idempotent: false),
                    $"{Network} returned {(int)published.StatusCode}: {body}", ThreadParts.IsAmbiguous(published.StatusCode));
            }
        }
        catch (Exception ex)
        {
            return (null, PostOutcome.FromException(Network, ex, idempotent: false), ex.Message, true);
        }

        using var result = JsonDocument.Parse(body);
        return (result.RootElement.GetProperty("id").GetString(), null, null, false);
    }

    private async Task<(ActionResult Failure, string Detail)?> WaitUntilProcessedAsync(HttpClient client, string containerId, CancellationToken cancellationToken)
    {
        for (var check = 0; check < MaxStatusChecks; check++)
        {
            using var response = await client.GetAsync($"{Graph}{containerId}?fields=status,error_message", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (PostOutcome.FromPreparationResponse(Network, "processing check", response.StatusCode, body), $"{Network} returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status is "FINISHED")
            {
                return null;
            }

            if (status is "ERROR" or "EXPIRED")
            {
                var reason = doc.RootElement.TryGetProperty("error_message", out var e) ? e.GetString() : null;
                var detail = $"Threads couldn't process the post ({status}{(string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason)}).";
                return (ActionResult.Failed(new InvalidOperationException(detail), StepRunErrorCategory.Validation), detail);
            }

            await Task.Delay(StatusPollDelay, cancellationToken);
        }

        const string slow = "Threads hadn't finished processing the post in time. Nothing was published.";
        return (ActionResult.Failed(new HttpRequestException(slow), StepRunErrorCategory.ServiceUnavailable), slow);
    }

    /// <summary>The post's permalink, or an empty string if Threads doesn't give one (the post is live either way).</summary>
    private static async Task<string> PermalinkAsync(HttpClient client, string mediaId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{Graph}{mediaId}?fields=permalink", cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("permalink", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return string.Empty;
        }
    }
}
