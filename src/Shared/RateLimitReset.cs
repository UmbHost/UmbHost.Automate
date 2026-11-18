using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UmbHost.Automate.Social;

/// <summary>
/// When a network says its rate limit resets, read from its answer to a refused (rate-limited) request, and the machine line that carries
/// it to UmbSocial: <c>UMBSOCIAL-RETRY-AT {"retryAtUtc":"2026-11-18T14:05:00Z"}</c>, appended to the step's error after the sentence.
/// </summary>
/// <remarks>
/// <para>
/// Automate's <c>ActionResult.Failed</c> carries no output data, so the step's error is the only way to hand UmbSocial the reset time,
/// the same way <see cref="ThreadParts"/> hands it a thread's progress. Neither Automate nor .NET reads a network's own rate-limit
/// headers, so this small reader lives here.
/// </para>
/// <para>
/// Each network is read only for what it documents (checked 2026-11-18), plus the standard <c>Retry-After</c> (RFC 9110, seconds or an
/// HTTP-date, https://www.rfc-editor.org/rfc/rfc9110#field.retry-after) for every network:
/// </para>
/// <list type="bullet">
/// <item>Bluesky: <c>RateLimit-Reset</c>, epoch seconds. https://docs.bsky.app/docs/advanced-guides/rate-limits says services "return rate
/// limit headers" without naming them; the PDS's own code (bluesky-social/atproto, packages/xrpc-server/src/rate-limiter-http.ts) sets
/// <c>RateLimit-Reset</c> to <c>Math.floor((Date.now() + msBeforeNext) / 1e3)</c>, and <c>Retry-After</c> in seconds on a 429.</item>
/// <item>Mastodon: <c>X-RateLimit-Reset</c>, an ISO 8601 timestamp. https://docs.joinmastodon.org/api/rate-limits/ calls it the
/// "Timestamp when your rate limit will reset"; Mastodon's code (config/initializers/rack_attack.rb) writes it with <c>iso8601(6)</c>.</item>
/// <item>X: <c>x-rate-limit-reset</c>, epoch seconds ("Unix timestamp when window resets"),
/// https://docs.x.com/x-api/fundamentals/rate-limits.</item>
/// <item>Facebook and Instagram (Graph): <c>X-Business-Use-Case-Usage</c>, JSON keyed by business object id, each an array of objects whose
/// <c>estimated_time_to_regain_access</c> is "Time, in minutes, until calls will not longer be throttled",
/// https://developers.facebook.com/docs/graph-api/overview/rate-limiting. <c>X-App-Usage</c> carries percentages only, no time.</item>
/// <item>Pinterest: <c>x-ratelimit-reset</c>, seconds until the window resets. https://developers.pinterest.com/docs/reference/rate-limits
/// shows <c>x-ratelimit-reset: 1</c> beside <c>x-ratelimit-limit: 100, 100;w=1, 1000;w=60</c> without defining it; that limit syntax is
/// the IETF RateLimit header draft's, where reset is delta-seconds, and a one-second window can only mean one second from now.</item>
/// <item>Google Business Profile: a <c>google.rpc.RetryInfo</c> in the error's <c>details</c>, <c>retryDelay</c> a protobuf Duration
/// such as <c>"30s"</c> ("Clients should wait until retry_delay amount of time has passed", googleapis google/rpc/error_details.proto;
/// the error shape is https://google.aip.dev/193). https://developers.google.com/my-business/content/limits names no header.</item>
/// <item>LinkedIn documents no header (https://learn.microsoft.com/en-us/linkedin/shared/api-guide/concepts/rate-limits: daily limits
/// that reset at midnight UTC, but a 429 can also be infrastructure protection, so midnight is not a reset time to rely on), and Threads
/// documents none (https://developers.facebook.com/docs/threads/overview, rate limiting): <c>Retry-After</c> only.</item>
/// </list>
/// <para>
/// When several signals are present the latest reset wins. A missing or unreadable value, or one already past, gives no time: this never
/// guesses. Nothing is clamped here; UmbSocial clamps.
/// </para>
/// </remarks>
internal static partial class RateLimitReset
{
    /// <summary>Prefix of the machine line that carries a rate limit's reset time.</summary>
    public const string TailPrefix = "UMBSOCIAL-RETRY-AT ";

    /// <summary>
    /// The reset time a network's rate-limited answer gives, rounded up to the second, or null when it gives none in the future.
    /// </summary>
    /// <param name="network">The network's display name, as the actions pass it (e.g. "Bluesky").</param>
    public static DateTimeOffset? Find(string network, HttpResponseHeaders? headers, string? body, DateTimeOffset now)
    {
        var candidates = new List<DateTimeOffset?> { RetryAfter(headers, now) };
        candidates.AddRange(network switch
        {
            "Bluesky" => [Epoch(Header(headers, "RateLimit-Reset"))],
            "Mastodon" => [Iso(Header(headers, "X-RateLimit-Reset"))],
            "X" => [Epoch(Header(headers, "x-rate-limit-reset"))],
            "Facebook" or "Instagram" => [BusinessUseCase(Header(headers, "X-Business-Use-Case-Usage"), now)],
            "Pinterest" => [Seconds(Header(headers, "x-ratelimit-reset"), now)],
            "Google Business Profile" => [RetryInfo(body, now)],
            _ => [],
        });

        var latest = candidates.Where(c => c > now).Max();
        return latest is { } at ? RoundUp(at) : null;
    }

    /// <summary>The machine line for a reset time, starting with a newline: <c>\nUMBSOCIAL-RETRY-AT {"retryAtUtc":"…Z"}</c>.</summary>
    public static string Tail(DateTimeOffset retryAt)
        => "\n" + TailPrefix + JsonSerializer.Serialize(new
        {
            retryAtUtc = RoundUp(retryAt).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });

    /// <summary>Reads the reset time back from a failure message, or null when it has no such line.</summary>
    public static DateTimeOffset? ParseTail(string message)
    {
        var line = message.Split('\n').LastOrDefault(l => l.StartsWith(TailPrefix, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(line[TailPrefix.Length..]);
        return DateTimeOffset.Parse(doc.RootElement.GetProperty("retryAtUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private static DateTimeOffset RoundUp(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        var whole = new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        return whole == utc ? whole : whole.AddSeconds(1);
    }

    private static string? Header(HttpResponseHeaders? headers, string name)
        => headers is not null && headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static DateTimeOffset? RetryAfter(HttpResponseHeaders? headers, DateTimeOffset now)
        => headers?.RetryAfter switch
        {
            { Delta: { } delta } => now + delta,
            { Date: { } date } => date,
            _ => null,
        };

    private static DateTimeOffset? Epoch(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= MaxEpochSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static DateTimeOffset? Seconds(string? value, DateTimeOffset now)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= MaxDeltaSeconds
            ? now.AddSeconds(seconds)
            : null;

    private static DateTimeOffset? Iso(string? value)
        => value is not null && IsoShape().IsMatch(value)
           && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>The latest <c>estimated_time_to_regain_access</c> (minutes) across every business object in the header.</summary>
    private static DateTimeOffset? BusinessUseCase(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double? minutes = null;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var usage in entry.Value.EnumerateArray())
                {
                    if (usage.ValueKind == JsonValueKind.Object
                        && usage.TryGetProperty("estimated_time_to_regain_access", out var time)
                        && time.ValueKind == JsonValueKind.Number
                        && time.TryGetDouble(out var m)
                        && m is > 0 and <= MaxDeltaSeconds / 60)
                    {
                        minutes = Math.Max(minutes ?? 0, m);
                    }
                }
            }

            return minutes is { } wait ? now.AddMinutes(wait) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>retryDelay</c> of a <c>google.rpc.RetryInfo</c> in a Google error's <c>details</c>.</summary>
    private static DateTimeOffset? RetryInfo(string? body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("@type", out var type) && type.GetString() == "type.googleapis.com/google.rpc.RetryInfo"
                    && detail.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String
                    && Duration().Match(delay.GetString()!) is { Success: true } match
                    && decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                    && seconds <= MaxDeltaSeconds)
                {
                    return now.AddSeconds((double)seconds);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Bounds that only keep the arithmetic in range (year 9999 and about a century); they are not a clamp.
    private const long MaxEpochSeconds = 253_402_300_799;
    private const long MaxDeltaSeconds = 3_153_600_000;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex IsoShape();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)s$")]
    private static partial Regex Duration();
}
