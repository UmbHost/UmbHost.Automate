using System.Net;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;

namespace UmbHost.Automate.Social;

/// <summary>
/// One post's public counts, as a provider's <c>getPostMetrics</c> action reports them.
/// </summary>
public sealed class PostMetricsResult
{
    /// <summary>The id as it was asked for.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>One of <see cref="PostMetricsState"/>.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The counts under the network's own names. Empty unless <see cref="State"/> is <c>ok</c>.</summary>
    public IReadOnlyDictionary<string, decimal> Values { get; init; } = new Dictionary<string, decimal>();

    /// <summary>Why the counts aren't there (one of <see cref="PostMetricsReason"/>), or null.</summary>
    public string? Reason { get; init; }

    internal static PostMetricsResult Ok(string id, IReadOnlyDictionary<string, decimal> values)
        => new() { Id = id, State = PostMetricsState.Ok, Values = values };

    internal static PostMetricsResult Gone(string id) => new() { Id = id, State = PostMetricsState.Gone };

    internal static PostMetricsResult Unavailable(string id, string reason)
        => new() { Id = id, State = PostMetricsState.Unavailable, Reason = reason };
}

/// <summary>The states a <see cref="PostMetricsResult"/> can be in.</summary>
internal static class PostMetricsState
{
    public const string Ok = "ok";
    public const string Unavailable = "unavailable";

    /// <summary>The network no longer has the post (deleted, or the id never existed). Never fetched again.</summary>
    public const string Gone = "gone";

    public const string NeedsPermission = "needsPermission";
}

/// <summary>The reasons a <see cref="PostMetricsResult"/> carries no counts.</summary>
internal static class PostMetricsReason
{
    /// <summary>The provider's insights setting is off.</summary>
    public const string InsightsOff = "insights_off";

    /// <summary>The connection lacks a permission the read needs.</summary>
    public const string ScopeMissing = "scope_missing";

    /// <summary>The network has no counts for this kind of post.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The network answered with a redirect, which a read never follows.</summary>
    public const string Redirected = "redirected";
}

/// <summary>
/// Reads the <c>Ids</c> setting every provider's <c>getPostMetrics</c> action shares.
/// </summary>
internal static class PostMetricsIds
{
    /// <summary>The longest id accepted (UmbSocial stores external ids in 500 characters).</summary>
    public const int MaxIdLength = 500;

    /// <summary>
    /// Parses a JSON array of 1 to <paramref name="max"/> non-empty strings of at most 500 characters,
    /// de-duplicated in first-seen order. Anything else is null.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? json, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        string?[]? ids;
        try
        {
            ids = JsonSerializer.Deserialize<string?[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (ids is null || ids.Length == 0 || ids.Length > max
            || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength))
        {
            return null;
        }

        return ids.Distinct(StringComparer.Ordinal).ToList()!;
    }

    /// <summary>The step failure for a setting <see cref="Parse"/> refused.</summary>
    public static ActionResult Invalid(int max = 100)
        => ActionResult.Failed(
            new ArgumentException($"Ids must be a JSON array of 1 to {max} non-empty post ids of at most {MaxIdLength} characters."),
            StepRunErrorCategory.Validation);
}

/// <summary>
/// Maps a failed metrics read onto an Automate step failure. Unlike a post (see <see cref="PostOutcome"/>), a read
/// changes nothing, so every transient failure is safe for Automate to retry.
/// </summary>
internal static class ReadOutcome
{
    /// <summary>401 → Authentication; 429 → RateLimiting; 408 and 5xx → ServiceUnavailable; any other 4xx → Validation.</summary>
    public static ActionResult FromResponse(string network, HttpStatusCode status, string body)
    {
        var code = (int)status;
        var detail = $"{network} returned {code}: {(body.Length <= 500 ? body : body[..500] + "…")}";
        var category = status switch
        {
            HttpStatusCode.Unauthorized => StepRunErrorCategory.Authentication,
            HttpStatusCode.TooManyRequests => StepRunErrorCategory.RateLimiting,
            HttpStatusCode.RequestTimeout => StepRunErrorCategory.ServiceUnavailable,
            _ when code is >= 400 and < 500 => StepRunErrorCategory.Validation,
            _ => StepRunErrorCategory.ServiceUnavailable,
        };

        return ActionResult.Failed(new HttpRequestException(detail), category);
    }

    /// <summary>A request that threw: cancelled by the caller → Cancelled; a timeout or network error → ServiceUnavailable.</summary>
    public static ActionResult FromException(Exception exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? ActionResult.Failed(exception, StepRunErrorCategory.Cancelled)
            : ActionResult.Failed(exception, StepRunErrorCategory.ServiceUnavailable);

    /// <summary>Whether the network answered with a redirect (3xx).</summary>
    public static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    /// <summary>Whether a thrown exception is one <see cref="FromException"/> settles (a transport failure, not a bug).</summary>
    public static bool IsTransport(Exception exception)
        => exception is HttpRequestException or OperationCanceledException;

    /// <summary>Reads the named integer counts that are present on a JSON object.</summary>
    public static IReadOnlyDictionary<string, decimal> Counts(JsonElement item, IEnumerable<string> names)
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                values[name] = number;
            }
        }

        return values;
    }
}
