using System.Net;
using System.Net.Http.Headers;
using Umbraco.Automate.Core.Actions;

namespace UmbHost.Automate.Social;

/// <summary>
/// Maps the outcome of a social network's "create post" request onto an Automate step failure,
/// choosing the error category so that Automate never retries a request that may already have
/// published the post.
/// </summary>
/// <remarks>
/// <para>
/// Automate retries a failed step (up to 10 times by default) unless its category is terminal
/// (Validation, ConfigurationError, Authentication, Cancelled). A retried post request is a
/// duplicate public post, so the rules here are:
/// </para>
/// <list type="bullet">
/// <item>401 → Authentication (terminal).</item>
/// <item>429 → RateLimiting (retried). The network refused the request, so nothing was posted.</item>
/// <item>Any other 4xx → Validation (terminal). The network rejected the content.</item>
/// <item>5xx, a timeout or a network error → the post may or may not exist. For networks that
/// honour an idempotency key this is retried; otherwise it is reported as terminal with a message
/// telling the operator to check the account before re-running.</item>
/// </list>
/// <para>
/// A RateLimiting failure whose answer says when the limit resets (see <see cref="RateLimitReset"/> for what each network sends) carries
/// one machine line after the sentence: <c>UMBSOCIAL-RETRY-AT {"retryAtUtc":"2026-11-18T14:05:00Z"}</c> (UTC, seconds). No reset time
/// in the answer, no line. The exception type and the category stay as they are.
/// </para>
/// </remarks>
internal static class PostOutcome
{
    internal const string MayHavePostedMessage =
        "The post may have been published: the network did not confirm the outcome. It was not retried, " +
        "to avoid a duplicate. Check the account before re-running this step.";

    /// <summary>
    /// Builds the failure for a non-success response from the create-post request. The step's error is the network's own words
    /// (<see cref="NetworkError.Plain"/>); the whole answer goes to the step's log. With <paramref name="graph"/> (Facebook, Instagram,
    /// Threads), a Graph error code settles the category where the status alone can't (Graph answers an expired token with 400).
    /// Pass the answer's <paramref name="headers"/> so a rate limit's reset time reaches UmbSocial.
    /// </summary>
    public static ActionResult FromResponse(
        ActionContext? context, string network, HttpStatusCode status, string body, bool idempotent, bool graph = false,
        HttpResponseHeaders? headers = null, TimeProvider? clock = null)
    {
        var code = (int)status;
        NetworkError.Log(context, network, null, status, body);
        var detail = NetworkError.Plain(network, null, status, body);

        if (graph && code is >= 400 and < 500 && GraphCategory(body) is { } category)
        {
            return ActionResult.Failed(
                category == StepRunErrorCategory.Authentication
                    ? new UnauthorizedAccessException(detail)
                    : new HttpRequestException(WithRetryAt(detail, category, network, headers, body, clock)),
                category);
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            return ActionResult.Failed(new UnauthorizedAccessException(detail), StepRunErrorCategory.Authentication);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return ActionResult.Failed(
                new HttpRequestException(WithRetryAt(detail, StepRunErrorCategory.RateLimiting, network, headers, body, clock)),
                StepRunErrorCategory.RateLimiting);
        }

        if (code is >= 400 and < 500)
        {
            return ActionResult.Failed(new InvalidOperationException(detail), StepRunErrorCategory.Validation);
        }

        return Ambiguous(network, new HttpRequestException(detail), idempotent);
    }

    /// <summary>
    /// Builds the failure for a create-post request that threw before a response arrived.
    /// </summary>
    public static ActionResult FromException(string network, Exception exception, bool idempotent)
        => exception is OperationCanceledException { CancellationToken.IsCancellationRequested: true }
            ? ActionResult.Failed(exception, StepRunErrorCategory.Cancelled)
            : Ambiguous(network, exception, idempotent);

    private static ActionResult Ambiguous(string network, Exception exception, bool idempotent)
        => idempotent
            ? ActionResult.Failed(exception, StepRunErrorCategory.ServiceUnavailable)
            : ActionResult.Failed(
                new InvalidOperationException($"{network}: {MayHavePostedMessage}", exception),
                StepRunErrorCategory.ConfigurationError);

    /// <summary>
    /// Builds the failure for a request made before the post itself (sign-in, media upload).
    /// Nothing has been published yet, so transient failures are safe to retry. Pass the answer's <paramref name="headers"/> so a rate
    /// limit's reset time reaches UmbSocial.
    /// </summary>
    public static ActionResult FromPreparationResponse(
        ActionContext? context, string network, string stage, HttpStatusCode status, string body, bool graph = false,
        HttpResponseHeaders? headers = null, TimeProvider? clock = null)
    {
        var code = (int)status;
        NetworkError.Log(context, network, stage, status, body);
        var detail = NetworkError.Plain(network, stage, status, body);
        var category = (graph && code is >= 400 and < 500 ? GraphCategory(body) : null) ?? status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StepRunErrorCategory.Authentication,
            HttpStatusCode.TooManyRequests => StepRunErrorCategory.RateLimiting,
            _ when code is >= 400 and < 500 => StepRunErrorCategory.Validation,
            _ => StepRunErrorCategory.ServiceUnavailable,
        };

        return ActionResult.Failed(new HttpRequestException(WithRetryAt(detail, category, network, headers, body, clock)), category);
    }

    /// <summary>The sentence, plus the <see cref="RateLimitReset"/> line when this is a rate limit whose answer says when it resets.</summary>
    private static string WithRetryAt(
        string detail, StepRunErrorCategory category, string network, HttpResponseHeaders? headers, string body, TimeProvider? clock)
        => category == StepRunErrorCategory.RateLimiting
           && RateLimitReset.Find(network, headers, body, (clock ?? TimeProvider.System).GetUtcNow()) is { } retryAt
            ? detail + RateLimitReset.Tail(retryAt)
            : detail;

    /// <summary>
    /// The category a Graph error code means, or null to go by the status. From
    /// https://developers.facebook.com/docs/graph-api/guides/error-handling (checked 2026-11-18): 190 "Access token has expired" and 102
    /// "API Session" (get a new token), 10 "API Permission Denied" and 200-299 permissions (reconnect with the permission) are the
    /// sign-in; 4 and 17 are throttling. 32 and 613 as throttling are from Meta's rate-limiting guide, as in <c>GraphError</c>.
    /// </summary>
    internal static StepRunErrorCategory? GraphCategory(string body)
        => NetworkError.GraphCode(body) switch
        {
            190 or 102 or 10 or (>= 200 and <= 299) => StepRunErrorCategory.Authentication,
            4 or 17 or 32 or 613 => StepRunErrorCategory.RateLimiting,
            _ => null,
        };
}
