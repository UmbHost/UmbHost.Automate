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
    Icon = "icon-twitter-x",
    ConnectionTypeAlias = "x")]
public sealed class CreatePostAction : ActionBase<CreatePostSettings, CreatePostOutput>
{
    private const string Network = "X";
    private const long MaxImageBytes = 5 * 1024 * 1024;

    // https://docs.x.com/x-api/media/quickstart/best-practices (checked 2026-11-18): "Animated GIF size: <= 15 MB"; "In order to process
    // larger GIFs, use the chunked upload endpoint with the media_category parameter"; "Pass media_category=tweet_gif to enable async upload
    // behavior for Posts with an animated GIF". So every GIF goes through the chunked upload as tweet_gif, up to 15 MiB; other images stay
    // at 5 MiB on the one-request upload.
    private const long MaxGifBytes = 15 * 1024 * 1024;

    // https://docs.x.com/x-api/media/quickstart/media-upload-chunked (checked 2026-11-18): keep each APPEND segment at or below 5 MB (the
    // server's maximum is 8 MB).
    private const int SegmentBytes = 4 * 1024 * 1024;

    /// <summary>How often a GIF's processing is checked before the post gives up (nothing is posted yet, so it is safe to retry).</summary>
    private const int MaxStatusChecks = 30;
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

    /// <summary>The wait between processing checks when X doesn't say (<c>check_after_secs</c>); tests set it to zero, which also skips X's own wait.</summary>
    internal TimeSpan? StatusPollDelay { get; init; }

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

        var (threadParts, threadError) = ThreadParts.Parse(settings.ThreadParts);
        if (threadError is not null)
        {
            return ActionResult.Failed(new ArgumentException(threadError), StepRunErrorCategory.Validation);
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
            var (mediaId, failure) = await UploadImageAsync(context, client, settings, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            post["media"] = new JsonObject { ["media_ids"] = new JsonArray(mediaId) };
        }

        var (firstId, firstFailure, _, _) = await SendAsync(context, client, post, cancellationToken);
        if (firstId is null)
        {
            return firstFailure!;
        }

        var urls = new List<string> { UrlOf(firstId) };
        var previousId = firstId;
        for (var i = 0; i < threadParts.Count; i++)
        {
            var part = new JsonObject
            {
                ["text"] = threadParts[i],
                ["reply"] = new JsonObject { ["in_reply_to_tweet_id"] = previousId },
            };

            string? id;
            string? detail;
            bool unknown;
            try
            {
                (id, _, detail, unknown) = await SendAsync(context, client, part, cancellationToken);
            }
            catch (Exception ex)
            {
                // Earlier parts are live: whatever went wrong, end the thread here rather than let a retry post them again.
                (id, detail, unknown) = (null, ex.Message, true);
            }

            if (id is null)
            {
                return ThreadParts.PartFailed(Network, i + 2, threadParts.Count + 1, urls, detail!, unknown);
            }

            urls.Add(UrlOf(id));
            previousId = id;
        }

        return Success(new CreatePostOutput { PostId = firstId, PostUrl = urls[0], ThreadUrls = urls });
    }

    // X redirects /i/web/status/{id} to the post under its author's handle.
    private static string UrlOf(string id) => id.Length > 0 ? $"https://x.com/i/web/status/{id}" : string.Empty;

    /// <summary>Creates one post. On failure returns the step failure (first post) and a plain detail (later parts).</summary>
    private static async Task<(string? Id, ActionResult? Failure, string? Detail, bool Unknown)> SendAsync(
        ActionContext context, HttpClient client, JsonObject post, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var response = await client.PostAsJsonAsync("tweets", post, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromResponse(context, Network, response.StatusCode, body, idempotent: false), NetworkError.Plain(Network, null, response.StatusCode, body), ThreadParts.IsAmbiguous(response.StatusCode));
            }

            // Read inside the try: X accepted the post, so an answer that can't be read leaves it live, never retried.
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var i)
                ? i.GetString() ?? string.Empty
                : string.Empty;
            return (id, null, null, false);
        }
        catch (Exception ex)
        {
            return (null, PostOutcome.FromException(Network, ex, idempotent: false), ex.Message, true);
        }
    }

    private async Task<(string? MediaId, ActionResult? Failure)> UploadImageAsync(
        ActionContext context, HttpClient client, CreatePostSettings settings, CancellationToken cancellationToken)
    {
        // Fetched up to the GIF limit; anything but a GIF is then held to the image limit.
        var (image, imageError) = await _imageFetcher.FetchAsync(settings.ImageUrl!, MaxGifBytes, cancellationToken);
        if (image is null)
        {
            return (null, ActionResult.Failed(new ArgumentException(imageError), StepRunErrorCategory.Validation));
        }

        var gif = image.ContentType == "image/gif";
        if (!gif && image.Bytes.LongLength > MaxImageBytes)
        {
            return (null, ActionResult.Failed(
                new ArgumentException($"The image is {image.Bytes.LongLength} bytes; the limit is {MaxImageBytes} bytes."), StepRunErrorCategory.Validation));
        }

        string mediaId;
        if (gif)
        {
            var (gifId, gifFailure) = await UploadGifAsync(context, client, image, cancellationToken);
            if (gifFailure is not null)
            {
                return (null, gifFailure);
            }

            mediaId = gifId!;
        }
        else
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(image.Bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
            form.Add(file, "media", image.FileName);
            form.Add(new StringContent("tweet_image"), "media_category");

            using var upload = await client.PostAsync("media/upload", form, cancellationToken);
            var uploadBody = await upload.Content.ReadAsStringAsync(cancellationToken);
            if (!upload.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "image upload", upload.StatusCode, uploadBody));
            }

            using var doc = JsonDocument.Parse(uploadBody);
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
                return (null, PostOutcome.FromPreparationResponse(context,
                    Network, "image alt text", alt.StatusCode, await alt.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        return (mediaId, null);
    }

    /// <summary>
    /// A GIF through X's chunked upload (https://docs.x.com/x-api/media/quickstart/media-upload-chunked): <c>POST media/upload/initialize</c>
    /// (JSON: <c>media_type</c>, <c>total_bytes</c>, <c>media_category</c> <c>tweet_gif</c>), <c>POST media/upload/{id}/append</c> per
    /// segment (multipart: <c>segment_index</c> from 0, <c>media</c>), <c>POST media/upload/{id}/finalize</c>, then, while
    /// <c>processing_info.state</c> is <c>pending</c> or <c>in_progress</c>, <c>GET media/upload?command=STATUS&amp;media_id={id}</c> after
    /// <c>check_after_secs</c> until <c>succeeded</c> or <c>failed</c>. Nothing is posted until the post itself, so every failure here is a
    /// preparation failure.
    /// </summary>
    private async Task<(string? MediaId, ActionResult? Failure)> UploadGifAsync(ActionContext context, HttpClient client, FetchedImage image, CancellationToken cancellationToken)
    {
        string mediaId;
        using (var init = await client.PostAsJsonAsync(
                   "media/upload/initialize",
                   new { media_type = "image/gif", total_bytes = image.Bytes.LongLength, media_category = "tweet_gif" },
                   cancellationToken))
        {
            var initBody = await init.Content.ReadAsStringAsync(cancellationToken);
            if (!init.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "GIF upload", init.StatusCode, initBody));
            }

            using var doc = JsonDocument.Parse(initBody);
            mediaId = doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
        }

        var id = Uri.EscapeDataString(mediaId);
        for (var (offset, index) = (0, 0); offset < image.Bytes.Length; offset += SegmentBytes, index++)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(index.ToString(System.Globalization.CultureInfo.InvariantCulture)), "segment_index");
            var segment = new ByteArrayContent(image.Bytes, offset, Math.Min(SegmentBytes, image.Bytes.Length - offset));
            segment.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(segment, "media", image.FileName);

            using var append = await client.PostAsync($"media/upload/{id}/append", form, cancellationToken);
            if (!append.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "GIF upload", append.StatusCode,
                    await append.Content.ReadAsStringAsync(cancellationToken)));
            }
        }

        string finalizeBody;
        using (var finalize = await client.PostAsync($"media/upload/{id}/finalize", null, cancellationToken))
        {
            finalizeBody = await finalize.Content.ReadAsStringAsync(cancellationToken);
            if (!finalize.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "GIF upload", finalize.StatusCode, finalizeBody));
            }
        }

        var processing = Processing(finalizeBody);
        for (var check = 0; processing is { State: "pending" or "in_progress" }; check++)
        {
            if (check >= MaxStatusChecks)
            {
                const string slow = "X hadn't finished processing the GIF in time. Nothing was posted.";
                return (null, ActionResult.Failed(new HttpRequestException(slow), StepRunErrorCategory.ServiceUnavailable));
            }

            await Task.Delay(StatusPollDelay ?? TimeSpan.FromSeconds(Math.Clamp(processing.CheckAfterSecs ?? 1, 1, 30)), cancellationToken);
            using var status = await client.GetAsync($"media/upload?command=STATUS&media_id={id}", cancellationToken);
            var statusBody = await status.Content.ReadAsStringAsync(cancellationToken);
            if (!status.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromPreparationResponse(context, Network, "GIF processing check", status.StatusCode, statusBody));
            }

            processing = Processing(statusBody);
        }

        if (processing is { State: "failed" })
        {
            NetworkError.Log(context, Network, "GIF processing", System.Net.HttpStatusCode.OK, processing.Body);
            var reason = processing.Error is { Length: > 0 } words ? $": {words}" : ".";
            return (null, ActionResult.Failed(new InvalidOperationException($"X couldn't process the GIF{reason}"), StepRunErrorCategory.Validation));
        }

        return (mediaId, null);
    }

    /// <summary>A GIF's <c>data.processing_info</c> (null when X sends none: it is ready).</summary>
    private sealed record GifProcessing(string? State, int? CheckAfterSecs, string? Error, string Body);

    private static GifProcessing? Processing(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // An answer without processing_info (or none at all) leaves the GIF as uploaded: X says when it isn't ready.
            return null;
        }

        using var owned = doc;
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("processing_info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = info.TryGetProperty("state", out var s) ? s.GetString() : null;
        int? after = info.TryGetProperty("check_after_secs", out var a) && a.TryGetInt32(out var secs) ? secs : null;
        string? error = info.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            ? (e.TryGetProperty("message", out var m) ? m.GetString() : null) ?? (e.TryGetProperty("name", out var n) ? n.GetString() : null)
            : null;
        return new GifProcessing(state, after, error, body);
    }
}
