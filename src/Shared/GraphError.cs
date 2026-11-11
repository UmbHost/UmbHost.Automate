using System.Net;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;

namespace UmbHost.Automate.Social;

/// <summary>
/// Classifies a failed Graph API read (Facebook, Instagram, Threads) from its status and its
/// <c>{"error":{"code","error_subcode",…}}</c> body, so one post's failure settles that post alone where it can.
/// </summary>
/// <remarks>
/// Codes from https://developers.facebook.com/docs/graph-api/guides/error-handling: 190 "Access token has expired", 10 "API Permission
/// Denied", 200-299 permission errors, 4 and 17 throttling. <b>UNVERIFIED</b>: 32 and 613 as throttling (Meta's rate-limiting guide,
/// not re-checked), and code 100 with <c>error_subcode</c> 33 as "the object doesn't exist" (no fetched Meta page documents it).
/// </remarks>
internal static class GraphError
{
    /// <summary>What a failed read means for the post, or for the whole step.</summary>
    public enum Kind
    {
        /// <summary>The token is expired or revoked: the step fails (Authentication).</summary>
        Authentication,

        /// <summary>Throttled: the step fails and is retried.</summary>
        RateLimited,

        /// <summary>The token lacks a permission: this post is <c>needsPermission</c> / <c>scope_missing</c>.</summary>
        Permission,

        /// <summary>The post doesn't exist (any more): this post is <c>gone</c>.</summary>
        NotFound,

        /// <summary>Anything else: settled by <see cref="ReadOutcome.FromResponse"/> or by the caller.</summary>
        Other,
    }

    /// <summary>Reads the error's code and subcode; nulls when the body isn't a Graph error.</summary>
    public static (int? Code, int? Subcode) Read(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (Int(error, "code"), Int(error, "error_subcode"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Classifies a non-success answer.</summary>
    public static Kind Classify(HttpStatusCode status, string body)
    {
        var (code, subcode) = Read(body);
        return code switch
        {
            190 => Kind.Authentication,
            4 or 17 or 32 or 613 => Kind.RateLimited,
            10 or (>= 200 and <= 299) => Kind.Permission,
            100 when subcode == 33 => Kind.NotFound,
            _ => status switch
            {
                HttpStatusCode.Unauthorized => Kind.Authentication,
                HttpStatusCode.TooManyRequests => Kind.RateLimited,
                HttpStatusCode.Forbidden => Kind.Permission,
                HttpStatusCode.NotFound => Kind.NotFound,
                _ => Kind.Other,
            },
        };
    }

    /// <summary>The step failure for an <see cref="Kind.Authentication"/>, <see cref="Kind.RateLimited"/> or <see cref="Kind.Other"/> answer.</summary>
    public static ActionResult Fail(string network, Kind kind, HttpStatusCode status, string body)
        => kind switch
        {
            Kind.Authentication => ReadOutcome.FromResponse(network, HttpStatusCode.Unauthorized, body),
            Kind.RateLimited => ReadOutcome.FromResponse(network, HttpStatusCode.TooManyRequests, body),
            _ => ReadOutcome.FromResponse(network, status, body),
        };

    private static int? Int(JsonElement error, string name)
        => error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>
/// Reads a Graph insights answer, <c>{"data":[{"name","period","values":[{"value"}]}]}</c>, as Threads
/// (https://developers.facebook.com/docs/threads/insights), Facebook (https://developers.facebook.com/documentation/pages-api/platforminsights/page.md)
/// and Instagram (https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights) document it.
/// </summary>
internal static class GraphInsights
{
    /// <summary>
    /// Each metric's value under its own name: <c>values[0].value</c>, or <c>total_value.value</c> where Instagram sends that instead.
    /// Metrics with no numeric value are left out. Throws <see cref="JsonException"/>, <see cref="KeyNotFoundException"/> or
    /// <see cref="InvalidOperationException"/> on an answer that isn't an insights answer.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Read(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (metric.GetProperty("name").GetString() is not { } name)
            {
                continue;
            }

            JsonElement value = default;
            var found = (metric.TryGetProperty("values", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                         && list[0].TryGetProperty("value", out value))
                        || (metric.TryGetProperty("total_value", out var total) && total.ValueKind == JsonValueKind.Object
                            && total.TryGetProperty("value", out value));
            if (found && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                values[name] = number;
            }
        }

        return values;
    }
}
