using System.Net;
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
/// </remarks>
internal static class PostOutcome
{
    internal const string MayHavePostedMessage =
        "The post may have been published: the network did not confirm the outcome. It was not retried, " +
        "to avoid a duplicate. Check the account before re-running this step.";

    /// <summary>
    /// Builds the failure for a non-success response from the create-post request.
    /// </summary>
    public static ActionResult FromResponse(string network, HttpStatusCode status, string body, bool idempotent)
    {
        var code = (int)status;
        var detail = $"{network} returned {code}: {Truncate(body)}";

        if (status == HttpStatusCode.Unauthorized)
        {
            return ActionResult.Failed(new UnauthorizedAccessException(detail), StepRunErrorCategory.Authentication);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            return ActionResult.Failed(new HttpRequestException(detail), StepRunErrorCategory.RateLimiting);
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
    /// Nothing has been published yet, so transient failures are safe to retry.
    /// </summary>
    public static ActionResult FromPreparationResponse(string network, string stage, HttpStatusCode status, string body)
    {
        var code = (int)status;
        var detail = $"{network} {stage} returned {code}: {Truncate(body)}";
        var category = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StepRunErrorCategory.Authentication,
            HttpStatusCode.TooManyRequests => StepRunErrorCategory.RateLimiting,
            _ when code is >= 400 and < 500 => StepRunErrorCategory.Validation,
            _ => StepRunErrorCategory.ServiceUnavailable,
        };

        return ActionResult.Failed(new HttpRequestException(detail), category);
    }

    private static string Truncate(string body) => body.Length <= 500 ? body : body[..500] + "…";
}
