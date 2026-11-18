using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Connection;
using UmbHost.Automate.Mastodon.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Publishes a status to Mastodon, optionally with one image.
/// </summary>
/// <remarks>
/// Mastodon honours an <c>Idempotency-Key</c> for an hour. The key is the run and step id, which stay
/// the same when Automate retries the step, so a retry after an unconfirmed request returns the
/// original status instead of posting a second one.
/// </remarks>
[Action("mastodon.createStatus", "Create Mastodon Status",
    Description = "Publishes a status to Mastodon.",
    Group = "Social",
    Icon = "icon-mastodon",
    ConnectionTypeAlias = "mastodon")]
public sealed class CreateStatusAction : ActionBase<CreateStatusSettings, CreateStatusOutput>
{
    private const string Network = "Mastodon";

    // Mastodon's default image_size_limit; instances may set it lower, which the upload reports.
    private const long MaxImageBytes = 16 * 1024 * 1024;
    private const int MaxMediaPolls = 10;

    private static readonly string[] Visibilities = ["public", "unlisted", "private", "direct"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImageFetcher _imageFetcher;

    public CreateStatusAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HttpClientFactoryOptions> httpClientOptions)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _imageFetcher = new ImageFetcher(httpClientFactory, httpClientOptions);
    }

    /// <summary>
    /// Wait between checks on an image the instance is still processing.
    /// </summary>
    internal TimeSpan MediaPollDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateStatusSettings>();

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        var visibility = string.IsNullOrWhiteSpace(settings.Visibility) ? "public" : settings.Visibility.Trim().ToLowerInvariant();
        if (!Visibilities.Contains(visibility))
        {
            return ActionResult.Failed(
                new ArgumentException($"Visibility must be one of: {string.Join(", ", Visibilities)}."),
                StepRunErrorCategory.Validation);
        }

        var (threadParts, threadError) = ThreadParts.Parse(settings.ThreadParts);
        if (threadError is not null)
        {
            return ActionResult.Failed(new ArgumentException(threadError), StepRunErrorCategory.Validation);
        }

        return ThreadParts.FirstPartFailed(await PublishAsync(context, settings, visibility, threadParts, cancellationToken), threadParts.Count + 1);
    }

    /// <summary>Prepares and posts the thread. Once the first post is live, a failure is only ever <see cref="ThreadParts.PartFailed"/>.</summary>
    private async Task<ActionResult> PublishAsync(
        ActionContext context, CreateStatusSettings settings, string visibility, IReadOnlyList<string> threadParts, CancellationToken cancellationToken)
    {
        var conn = context.Connection?.GetSettings<MastodonConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.InstanceUrl) || string.IsNullOrWhiteSpace(conn.AccessToken))
        {
            return ActionResult.Failed(new InvalidOperationException("A Mastodon connection is required."), StepRunErrorCategory.Authentication);
        }

        using var client = MastodonClient.Create(_httpClientFactory, conn);

        var status = new Dictionary<string, object> { ["status"] = settings.Text, ["visibility"] = visibility };
        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            status["language"] = settings.Language;
        }

        if (!string.IsNullOrWhiteSpace(settings.ContentWarning))
        {
            status["spoiler_text"] = settings.ContentWarning;
        }

        if (!string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            var (mediaId, failure) = await UploadImageAsync(context, client, settings, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            status["media_ids"] = new[] { mediaId! };
        }

        var (first, firstFailure, _, _) = await SendAsync(context, client, status, $"{context.RunId}:{context.StepId}", cancellationToken);
        if (first is null)
        {
            return firstFailure!;
        }

        var urls = new List<string> { first.Url };
        var previousId = first.Id;
        for (var i = 0; i < threadParts.Count; i++)
        {
            var part = new Dictionary<string, object>
            {
                ["status"] = threadParts[i],
                ["visibility"] = visibility,
                ["in_reply_to_id"] = previousId,
            };
            if (!string.IsNullOrWhiteSpace(settings.ContentWarning))
            {
                part["spoiler_text"] = settings.ContentWarning;
            }

            if (!string.IsNullOrWhiteSpace(settings.Language))
            {
                part["language"] = settings.Language;
            }

            // Each part has its own key, so a retry can't post a part twice; a failed part is still never retried.
            Posted? posted;
            string? detail;
            bool unknown;
            try
            {
                (posted, _, detail, unknown) = await SendAsync(context, client, part, $"{context.RunId}:{context.StepId}:{i + 1}", cancellationToken);
            }
            catch (Exception ex)
            {
                // Earlier parts are live: whatever went wrong, end the thread here rather than let a retry post them again.
                (posted, detail, unknown) = (null, ex.Message, true);
            }

            if (posted is null)
            {
                return ThreadParts.PartFailed(Network, i + 2, threadParts.Count + 1, urls, detail!, unknown);
            }

            urls.Add(posted.Url);
            previousId = posted.Id;
        }

        return Success(new CreateStatusOutput { StatusId = first.Id, PostUrl = first.Url, ThreadUrls = urls });
    }

    private sealed record Posted(string Id, string Url);

    /// <summary>Posts one status. On failure returns the step failure (for the first status) and a plain detail (for a later part).</summary>
    private static async Task<(Posted? Posted, ActionResult? Failure, string? Detail, bool Unknown)> SendAsync(
        ActionContext context, HttpClient client, Dictionary<string, object> status, string idempotencyKey, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/statuses")
            {
                Content = JsonContent.Create(status),
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            using var response = await client.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, PostOutcome.FromResponse(context, Network, response.StatusCode, body, idempotent: true, headers: response.Headers), NetworkError.Plain(Network, null, response.StatusCode, body), ThreadParts.IsAmbiguous(response.StatusCode));
            }

            // Read inside the try: Mastodon accepted the status, so an answer that can't be read leaves it live. A retry of the first
            // status reuses its idempotency key; a later part ends the thread.
            using var doc = JsonDocument.Parse(body);
            return (new Posted(
                doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                doc.RootElement.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty), null, null, false);
        }
        catch (Exception ex)
        {
            return (null, PostOutcome.FromException(Network, ex, idempotent: true), ex.Message, true);
        }
    }

    private async Task<(string? MediaId, ActionResult? Failure)> UploadImageAsync(
        ActionContext context, HttpClient client, CreateStatusSettings settings, CancellationToken cancellationToken)
    {
        var (image, imageError) = await _imageFetcher.FetchAsync(settings.ImageUrl!, MaxImageBytes, cancellationToken);
        if (image is null)
        {
            return (null, ActionResult.Failed(new ArgumentException(imageError), StepRunErrorCategory.Validation));
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(file, "file", image.FileName);
        if (!string.IsNullOrWhiteSpace(settings.ImageAltText))
        {
            form.Add(new StringContent(settings.ImageAltText), "description");
        }

        using var upload = await client.PostAsync("v2/media", form, cancellationToken);
        var uploadBody = await upload.Content.ReadAsStringAsync(cancellationToken);
        if (!upload.IsSuccessStatusCode)
        {
            return (null, PostOutcome.FromPreparationResponse(context, Network, "image upload", upload.StatusCode, uploadBody, headers: upload.Headers));
        }

        string mediaId;
        using (var doc = JsonDocument.Parse(uploadBody))
        {
            mediaId = doc.RootElement.GetProperty("id").GetString()!;
        }

        // 200 means processed; 202 means the instance is still processing it, and attaching it to a
        // status before it finishes fails. GET returns 206 until it is ready.
        if (upload.StatusCode == HttpStatusCode.Accepted)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (attempt == MaxMediaPolls)
                {
                    return (null, ActionResult.Failed(
                        new TimeoutException("Mastodon is still processing the image."), StepRunErrorCategory.Timeout));
                }

                await Task.Delay(MediaPollDelay, cancellationToken);
                using var check = await client.GetAsync($"v1/media/{mediaId}", cancellationToken);
                if (check.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }

                if (check.StatusCode != HttpStatusCode.PartialContent)
                {
                    return (null, PostOutcome.FromPreparationResponse(context,
                        Network, "image processing", check.StatusCode, await check.Content.ReadAsStringAsync(cancellationToken), headers: check.Headers));
                }
            }
        }

        return (mediaId, null);
    }
}
