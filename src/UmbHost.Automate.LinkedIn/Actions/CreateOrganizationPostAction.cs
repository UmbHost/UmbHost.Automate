using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.LinkedIn.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Publishes a post as a LinkedIn company page, optionally with an article link or one image.
/// </summary>
/// <remarks>
/// Needs the <c>w_organization_social</c> scope (Community Management API) and a member who is an
/// administrator or content admin of the page. LinkedIn has no idempotency key, so the create
/// request is never retried once it may have reached LinkedIn (see <see cref="PostOutcome"/>).
/// </remarks>
[Action("linkedin.createOrganizationPost", "Create LinkedIn Company Post",
    Description = "Publishes a post as a LinkedIn company page.",
    Group = "Social",
    Icon = "icon-linkedin",
    ConnectionTypeAlias = "linkedin")]
public sealed class CreateOrganizationPostAction : ActionBase<CreateOrganizationPostSettings, CreateOrganizationPostOutput>
{
    private const string Network = "LinkedIn";
    private const long MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxImagePolls = 10;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;
    private readonly ImageFetcher _imageFetcher;

    public CreateOrganizationPostAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HttpClientFactoryOptions> httpClientOptions,
        IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
        _imageFetcher = new ImageFetcher(httpClientFactory, httpClientOptions);
    }

    /// <summary>
    /// Wait between checks on an uploaded image LinkedIn is still processing.
    /// </summary>
    internal TimeSpan ImagePollDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateOrganizationPostSettings>();

        if (!long.TryParse(settings.OrganizationId, out _))
        {
            return ActionResult.Failed(new ArgumentException("Organization ID must be the page's numeric id."), StepRunErrorCategory.Validation);
        }

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        var hasLink = !string.IsNullOrWhiteSpace(settings.LinkUrl);
        var hasImage = !string.IsNullOrWhiteSpace(settings.ImageUrl);
        if (hasLink && hasImage)
        {
            return ActionResult.Failed(new ArgumentException("A LinkedIn post can have a link or an image, not both."), StepRunErrorCategory.Validation);
        }

        if (hasLink && string.IsNullOrWhiteSpace(settings.LinkTitle))
        {
            return ActionResult.Failed(new ArgumentException("A link needs a title: LinkedIn doesn't fetch it from the page."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<LinkedInConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A LinkedIn connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The LinkedIn token has expired (they last 60 days). Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        using var client = LinkedInClient.Create(_httpClientFactory, token, conn.ApiVersion);
        var author = $"urn:li:organization:{settings.OrganizationId}";

        var post = new JsonObject
        {
            ["author"] = author,
            ["commentary"] = LittleText.Escape(settings.Text),
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

        if (hasLink)
        {
            var article = new JsonObject { ["source"] = settings.LinkUrl, ["title"] = settings.LinkTitle };
            if (!string.IsNullOrWhiteSpace(settings.LinkDescription))
            {
                article["description"] = settings.LinkDescription;
            }

            post["content"] = new JsonObject { ["article"] = article };
        }

        if (hasImage)
        {
            var (imageUrn, failure) = await UploadImageAsync(client, token, author, settings.ImageUrl!, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var media = new JsonObject { ["id"] = imageUrn };
            if (!string.IsNullOrWhiteSpace(settings.ImageAltText))
            {
                media["altText"] = settings.ImageAltText;
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
                return PostOutcome.FromResponse(Network, response.StatusCode, body, idempotent: false);
            }

            // 201 with an empty body; the new post's URN is in a header.
            postUrn = response.Headers.TryGetValues("x-restli-id", out var ids) ? ids.First() : string.Empty;
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        return Success(new CreateOrganizationPostOutput
        {
            PostUrn = postUrn,
            PostUrl = postUrn.Length > 0 ? $"https://www.linkedin.com/feed/update/{postUrn}/" : string.Empty,
        });
    }

    /// <summary>
    /// Registers an image with LinkedIn, uploads it, and waits until LinkedIn has processed it.
    /// </summary>
    private async Task<(string? ImageUrn, ActionResult? Failure)> UploadImageAsync(
        HttpClient client, string token, string owner, string imageUrl, CancellationToken cancellationToken)
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
            return (null, PostOutcome.FromPreparationResponse(Network, "image registration", init.StatusCode, initBody));
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
                return (null, PostOutcome.FromPreparationResponse(
                    Network, "image upload", uploaded.StatusCode, await uploaded.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        // The image moves WAITING_UPLOAD → PROCESSING → AVAILABLE; a post can only use it once AVAILABLE.
        for (var attempt = 0; attempt < MaxImagePolls; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ImagePollDelay, cancellationToken);
            }

            using var check = await client.GetAsync($"images/{Uri.EscapeDataString(imageUrn)}", cancellationToken);
            var body = await check.Content.ReadAsStringAsync(cancellationToken);
            if (!check.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(Network, "image status", check.StatusCode, body));
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
