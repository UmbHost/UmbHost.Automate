using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Connection;
using UmbHost.Automate.Bluesky.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Actions;

/// <summary>
/// Publishes a post to Bluesky, optionally with one image or, without one, a link card.
/// </summary>
/// <remarks>
/// Bluesky has no idempotency key, so the create request is never retried once it may have reached
/// the server (see <see cref="PostOutcome"/>). Signing in and uploading the image are safe to retry.
/// </remarks>
[Action("bluesky.createPost", "Create Bluesky Post",
    Description = "Publishes a post to Bluesky.",
    Group = "Social",
    Icon = "icon-cloud",
    ConnectionTypeAlias = "bluesky")]
public sealed class CreatePostAction : ActionBase<CreatePostSettings, CreatePostOutput>
{
    private const string Network = "Bluesky";
    private const int MaxGraphemes = 300;
    private const long MaxImageBytes = 2_000_000;
    private const long MaxThumbBytes = 1_000_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImageFetcher _imageFetcher;

    public CreatePostAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HttpClientFactoryOptions> httpClientOptions)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _imageFetcher = new ImageFetcher(httpClientFactory, httpClientOptions);
    }

    private static bool IsHttps(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Uploads a link card's thumbnail, or returns null when it can't be fetched or uploaded.</summary>
    private async Task<JsonNode?> UploadThumbAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var (image, _) = await _imageFetcher.FetchAsync(url, MaxThumbBytes, cancellationToken);
            if (image is null)
            {
                return null;
            }

            using var upload = new ByteArrayContent(image.Bytes);
            upload.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            using var response = await client.PostAsync("com.atproto.repo.uploadBlob", upload, cancellationToken);
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["blob"]?.DeepClone() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreatePostSettings>();

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        if (new StringInfo(settings.Text).LengthInTextElements > MaxGraphemes)
        {
            return ActionResult.Failed(
                new ArgumentException($"Bluesky posts are limited to {MaxGraphemes} characters."),
                StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<BlueskyConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.Identifier) || string.IsNullOrWhiteSpace(conn.AppPassword))
        {
            return ActionResult.Failed(new InvalidOperationException("A Bluesky connection is required."), StepRunErrorCategory.Authentication);
        }

        using var client = BlueskyClient.Create(_httpClientFactory, conn);

        var (session, sessionStatus, sessionBody) = await BlueskyClient.CreateSessionAsync(client, conn, cancellationToken);
        if (session is null)
        {
            return PostOutcome.FromPreparationResponse(Network, "sign-in", sessionStatus, sessionBody);
        }

        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = settings.Text,
            ["createdAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            record["langs"] = new JsonArray(settings.Language);
        }

        var facets = LinkFacets.Find(settings.Text);
        if (facets.Count > 0)
        {
            record["facets"] = new JsonArray(facets.Select(f => (JsonNode)new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = f.ByteStart, ["byteEnd"] = f.ByteEnd },
                ["features"] = new JsonArray(new JsonObject
                {
                    ["$type"] = "app.bsky.richtext.facet#link",
                    ["uri"] = f.Uri,
                }),
            }).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            var (image, imageError) = await _imageFetcher.FetchAsync(settings.ImageUrl, MaxImageBytes, cancellationToken);
            if (image is null)
            {
                return ActionResult.Failed(new ArgumentException(imageError), StepRunErrorCategory.Validation);
            }

            using var upload = new ByteArrayContent(image.Bytes);
            upload.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            using var uploadResponse = await client.PostAsync("com.atproto.repo.uploadBlob", upload, cancellationToken);
            var uploadBody = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                return PostOutcome.FromPreparationResponse(Network, "image upload", uploadResponse.StatusCode, uploadBody);
            }

            var blob = JsonNode.Parse(uploadBody)!["blob"]!.DeepClone();
            record["embed"] = new JsonObject
            {
                ["$type"] = "app.bsky.embed.images",
                ["images"] = new JsonArray(new JsonObject
                {
                    ["image"] = blob,
                    ["alt"] = settings.ImageAltText ?? string.Empty,
                }),
            };
        }

        else if (IsHttps(settings.LinkUrl))
        {
            // A link card (Bluesky shows one only when the post carries it). Its thumbnail is a nicety: if it can't be fetched or
            // uploaded, the card goes without it rather than the post failing.
            var external = new JsonObject
            {
                ["uri"] = settings.LinkUrl,
                ["title"] = settings.LinkTitle ?? string.Empty,
                ["description"] = settings.LinkDescription ?? string.Empty,
            };

            if (IsHttps(settings.LinkImageUrl) && await UploadThumbAsync(client, settings.LinkImageUrl!, cancellationToken) is { } thumb)
            {
                external["thumb"] = thumb;
            }

            record["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.external", ["external"] = external };
        }

        string body;
        try
        {
            using var response = await client.PostAsJsonAsync(
                "com.atproto.repo.createRecord",
                new JsonObject { ["repo"] = session.Did, ["collection"] = "app.bsky.feed.post", ["record"] = record },
                cancellationToken);
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

        // The post exists from here on: never fail, or a retry would publish it again.
        using var doc = JsonDocument.Parse(body);
        var uri = doc.RootElement.TryGetProperty("uri", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        var cid = doc.RootElement.TryGetProperty("cid", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var rkey = uri[(uri.LastIndexOf('/') + 1)..];

        return Success(new CreatePostOutput
        {
            Uri = uri,
            Cid = cid,
            PostUrl = $"https://bsky.app/profile/{session.Handle}/post/{rkey}",
        });
    }
}
