using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Social;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Actions;

/// <summary>
/// Publishes a post to X, optionally with one image.
/// </summary>
/// <remarks>
/// X has no idempotency key, so the create request is never retried once it may have reached X
/// (see <see cref="PostOutcome"/>). X bills each post, and a post containing a link at a higher rate.
/// </remarks>
[Action("x.createPost", "Create X Post",
    Description = "Publishes a post to X (Twitter).",
    Group = "Social",
    Icon = "icon-twitter",
    ConnectionTypeAlias = "x")]
public sealed class CreatePostAction : ActionBase<CreatePostSettings, CreatePostOutput>
{
    private const string Network = "X";
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxAltTextLength = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;
    private readonly ImageFetcher _imageFetcher;

    public CreatePostAction(
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

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreatePostSettings>();

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        if (settings.ImageAltText?.Length > MaxAltTextLength)
        {
            return ActionResult.Failed(new ArgumentException($"Alt text is limited to {MaxAltTextLength} characters."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<XConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("An X connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The X token is expired or revoked. Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        using var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://api.x.com/2/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var post = new JsonObject { ["text"] = settings.Text };

        if (!string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            var (mediaId, failure) = await UploadImageAsync(client, settings, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            post["media"] = new JsonObject { ["media_ids"] = new JsonArray(mediaId) };
        }

        string body;
        try
        {
            using var response = await client.PostAsJsonAsync("tweets", post, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PostOutcome.FromResponse(Network, response.StatusCode, body, idempotent: false);
            }
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var i)
            ? i.GetString() ?? string.Empty
            : string.Empty;

        return Success(new CreatePostOutput
        {
            PostId = id,

            // X redirects /i/web/status/{id} to the post under its author's handle.
            PostUrl = id.Length > 0 ? $"https://x.com/i/web/status/{id}" : string.Empty,
        });
    }

    private async Task<(string? MediaId, ActionResult? Failure)> UploadImageAsync(
        HttpClient client, CreatePostSettings settings, CancellationToken cancellationToken)
    {
        var (image, imageError) = await _imageFetcher.FetchAsync(settings.ImageUrl!, MaxImageBytes, cancellationToken);
        if (image is null)
        {
            return (null, ActionResult.Failed(new ArgumentException(imageError), StepRunErrorCategory.Validation));
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(file, "media", image.FileName);
        form.Add(new StringContent("tweet_image"), "media_category");

        using var upload = await client.PostAsync("media/upload", form, cancellationToken);
        var uploadBody = await upload.Content.ReadAsStringAsync(cancellationToken);
        if (!upload.IsSuccessStatusCode)
        {
            return (null, PostOutcome.FromPreparationResponse(Network, "image upload", upload.StatusCode, uploadBody));
        }

        string mediaId;
        using (var doc = JsonDocument.Parse(uploadBody))
        {
            mediaId = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        }

        if (!string.IsNullOrWhiteSpace(settings.ImageAltText))
        {
            using var alt = await client.PostAsJsonAsync(
                "media/metadata",
                new { id = mediaId, metadata = new { alt_text = new { text = settings.ImageAltText } } },
                cancellationToken);
            if (!alt.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(
                    Network, "image alt text", alt.StatusCode, await alt.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        return (mediaId, null);
    }
}
