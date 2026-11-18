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

        /// <summary>Any other 4xx (see <see cref="ReadOutcome.RejectsThePost"/>): this post is <c>unavailable</c> / <c>rejected</c>.</summary>
        Rejected,

        /// <summary>Anything else (408, 5xx, …): the step fails through <see cref="ReadOutcome.FromResponse"/>.</summary>
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
                _ when ReadOutcome.RejectsThePost(status) => Kind.Rejected,
                _ => Kind.Other,
            },
        };
    }

    /// <summary>The step failure for an <see cref="Kind.Authentication"/>, <see cref="Kind.RateLimited"/> or <see cref="Kind.Other"/> answer.</summary>
    public static ActionResult Fail(ActionContext? context, string network, Kind kind, HttpStatusCode status, string body)
        => kind switch
        {
            Kind.Authentication => ReadOutcome.FromResponse(context, network, HttpStatusCode.Unauthorized, body),
            Kind.RateLimited => ReadOutcome.FromResponse(context, network, HttpStatusCode.TooManyRequests, body),
            _ => ReadOutcome.FromResponse(context, network, status, body),
        };

    private static int? Int(JsonElement error, string name)
        => error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
