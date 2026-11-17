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
/// https://developers.facebook.com/docs/threads/posts and https://developers.facebook.com/docs/threads/reference/publishing. Every call
/// carries <c>access_token</c> as a parameter, as those pages show: in the form of a POST, in the query of a GET. Threads fetches the
/// image from its public URL. Replies set <c>reply_to_id</c> (https://developers.facebook.com/docs/threads/retrieve-and-manage-replies/create-replies).
/// </para>
/// <para>
/// Nothing is live until a container is published, so a failure before that is safe to retry. Threads has no idempotency key, so a
/// publish request with no clear answer is never retried (see <see cref="PostOutcome"/>). Once a part is live, nothing may make Automate
/// retry the step (a retry would post that part again): any failure of a later part, a timeout or an answer that can't be read included,
/// ends the thread with the <see cref="ThreadParts"/> tail, and the step never throws.
/// </para>
/// </remarks>
[Action("threads.createPost", "Create Threads Post",
    Description = "Publishes a post to Threads, with an optional image and further parts as a thread.",
    Group = "Social",
    Icon = "icon-umbhost-threads",
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

        using var client = _httpClientFactory.CreateClient();
        string? token;
        string userId;
        try
        {
            token = await _tokens.GetAsync(credentialsId, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                return ActionResult.Failed(
                    new UnauthorizedAccessException("The Threads token has expired or was revoked. Re-authorise the connection."),
                    StepRunErrorCategory.Authentication);
            }

            using var me = await client.GetAsync(Get("me", "id,username", token), cancellationToken);
            var meBody = await me.Content.ReadAsStringAsync(cancellationToken);
            if (!me.IsSuccessStatusCode)
            {
                return PostOutcome.FromPreparationResponse(Network, "account lookup", me.StatusCode, meBody);
            }

            using var doc = JsonDocument.Parse(meBody);
            userId = doc.RootElement.GetProperty("id").GetString()!;
        }
        catch (Exception ex)
        {
            // Nothing is live yet.
            return Preparation(ex, cancellationToken);
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

            Part step;
            try
            {
                step = await PostPartAsync(client, userId, token, fields, cancellationToken);
            }
            catch (Exception ex)
            {
                // PostPartAsync answers every failure itself; this only catches the unforeseen, which may have left the part live.
                step = new Part(null, PostOutcome.FromException(Network, ex, idempotent: false), ex.Message, true);
            }

            if (step.MediaId is null)
            {
                return i == 0
                    ? step.Failure!
                    : ThreadParts.PartFailed(Network, i + 1, parts.Count, urls, step.Detail!, step.Unknown);
            }

            firstId ??= step.MediaId;
            previousId = step.MediaId;
            urls.Add(await PermalinkAsync(client, step.MediaId, token, cancellationToken));
        }

        return Success(new CreateThreadsPostOutput { PostId = firstId!, PostUrl = urls[0], ThreadUrls = urls });
    }

    /// <summary>One part's outcome: its media id once live; otherwise the step failure (first part), a plain detail (later parts), and
    /// whether the part may be live.</summary>
    private sealed record Part(string? MediaId, ActionResult? Failure, string? Detail, bool Unknown);

    /// <summary>A GET with <c>access_token</c> in its query, as the Threads reference shows.</summary>
    private static string Get(string path, string fields, string token)
        => $"{Graph}{path}?fields={fields}&access_token={Uri.EscapeDataString(token)}";

    /// <summary>A POST form with <c>access_token</c> among its parameters, as the Threads reference shows.</summary>
    private static FormUrlEncodedContent Form(Dictionary<string, string> fields, string token)
        => new(fields.Append(KeyValuePair.Create("access_token", token)));

    /// <summary>A failure before anything is live: safe to retry, unless the run itself was cancelled.</summary>
    private static ActionResult Preparation(Exception ex, CancellationToken cancellationToken)
        => ActionResult.Failed(ex, ex is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? StepRunErrorCategory.Cancelled
            : StepRunErrorCategory.ServiceUnavailable);

    /// <summary>Creates, waits for and publishes one container. Never throws.</summary>
    private async Task<Part> PostPartAsync(HttpClient client, string userId, string token, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        string containerId;
        try
        {
            using var created = await client.PostAsync($"{Graph}{userId}/threads", Form(fields, token), cancellationToken);
            var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
            if (!created.IsSuccessStatusCode)
            {
                // Nothing is live until the container is published, so this is never an unknown outcome.
                return new Part(null, PostOutcome.FromPreparationResponse(Network, "post preparation", created.StatusCode, createdBody),
                    $"{Network} returned {(int)created.StatusCode}: {createdBody}", false);
            }

            using (var doc = JsonDocument.Parse(createdBody))
            {
                containerId = doc.RootElement.GetProperty("id").GetString()!;
            }

            var notReady = await WaitUntilProcessedAsync(client, containerId, token, cancellationToken);
            if (notReady is not null)
            {
                return new Part(null, notReady.Value.Failure, notReady.Value.Detail, false);
            }
        }
        catch (Exception ex)
        {
            // Still nothing published for this part.
            return new Part(null, Preparation(ex, cancellationToken), ex.Message, false);
        }

        try
        {
            using var published = await client.PostAsync(
                $"{Graph}{userId}/threads_publish",
                Form(new Dictionary<string, string> { ["creation_id"] = containerId }, token),
                cancellationToken);
            var body = await published.Content.ReadAsStringAsync(cancellationToken);
            if (!published.IsSuccessStatusCode)
            {
                return new Part(null, PostOutcome.FromResponse(Network, published.StatusCode, body, idempotent: false),
                    $"{Network} returned {(int)published.StatusCode}: {body}", ThreadParts.IsAmbiguous(published.StatusCode));
            }

            // Read inside the try: Threads accepted the publish, so an answer that can't be read leaves the part live, never retried.
            using var result = JsonDocument.Parse(body);
            return new Part(result.RootElement.GetProperty("id").GetString(), null, null, false);
        }
        catch (Exception ex)
        {
            return new Part(null, PostOutcome.FromException(Network, ex, idempotent: false), ex.Message, true);
        }
    }

    private async Task<(ActionResult Failure, string Detail)?> WaitUntilProcessedAsync(HttpClient client, string containerId, string token, CancellationToken cancellationToken)
    {
        for (var check = 0; check < MaxStatusChecks; check++)
        {
            using var response = await client.GetAsync(Get(containerId, "status,error_message", token), cancellationToken);
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

    /// <summary>The post's permalink, or an empty string if Threads doesn't give one: the post is live either way, so nothing here may fail the step.</summary>
    private static async Task<string> PermalinkAsync(HttpClient client, string mediaId, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(Get(mediaId, "permalink", token), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("permalink", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
