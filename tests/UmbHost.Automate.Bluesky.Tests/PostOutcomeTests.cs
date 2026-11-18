using System.Net;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The shared retry rules for create-post requests. The source is linked into every social
/// package, so it is tested once here.
/// </summary>
public class PostOutcomeTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.BadRequest, StepRunErrorCategory.Validation)]
    [InlineData(HttpStatusCode.Forbidden, StepRunErrorCategory.Validation)]
    [InlineData(HttpStatusCode.UnprocessableEntity, StepRunErrorCategory.Validation)]
    public void FromResponse_maps_definite_rejections(HttpStatusCode status, StepRunErrorCategory expected)
    {
        var result = PostOutcome.FromResponse(null, "Net", status, "nope", idempotent: false);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public void FromResponse_server_error_without_idempotency_is_terminal_and_says_it_may_have_posted()
    {
        var result = PostOutcome.FromResponse(null, "Net", HttpStatusCode.BadGateway, "", idempotent: false);

        // ConfigurationError is one of Automate's terminal categories, so the step is not retried.
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public void FromResponse_server_error_with_idempotency_is_retryable()
    {
        var result = PostOutcome.FromResponse(null, "Net", HttpStatusCode.BadGateway, "", idempotent: true);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
    }

    [Fact]
    public void FromException_timeout_without_idempotency_is_terminal()
    {
        var result = PostOutcome.FromException("Net", new TaskCanceledException("timed out"), idempotent: false);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }

    [Fact]
    public void FromException_caller_cancellation_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = PostOutcome.FromException("Net", new OperationCanceledException(cts.Token), idempotent: false);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Cancelled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.BadRequest, StepRunErrorCategory.Validation)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StepRunErrorCategory.ServiceUnavailable)]
    public void FromPreparationResponse_retries_server_errors_because_nothing_was_posted(
        HttpStatusCode status, StepRunErrorCategory expected)
    {
        var result = PostOutcome.FromPreparationResponse(null, "Net", "upload", status, "");

        result.ErrorCategory.Should().Be(expected);
    }

    // Graph answers an expired token, a missing permission or throttling with 400 and says which in error.code
    // (https://developers.facebook.com/docs/graph-api/guides/error-handling).
    [Theory]
    [InlineData(190, StepRunErrorCategory.Authentication)]
    [InlineData(102, StepRunErrorCategory.Authentication)]
    [InlineData(10, StepRunErrorCategory.Authentication)]
    [InlineData(200, StepRunErrorCategory.Authentication)]
    [InlineData(4, StepRunErrorCategory.RateLimiting)]
    [InlineData(17, StepRunErrorCategory.RateLimiting)]
    [InlineData(100, StepRunErrorCategory.Validation)]
    public void A_graph_error_code_settles_the_category_for_graph_networks(int code, StepRunErrorCategory expected)
    {
        var body = $$$"""{"error":{"message":"m","type":"OAuthException","code":{{{code}}}}}""";

        PostOutcome.FromResponse(null, "Net", HttpStatusCode.BadRequest, body, idempotent: false, graph: true).ErrorCategory.Should().Be(expected);
        PostOutcome.FromPreparationResponse(null, "Net", "upload", HttpStatusCode.BadRequest, body, graph: true).ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public void Graph_codes_are_opt_in_because_google_puts_the_http_status_in_error_code()
        => PostOutcome.FromResponse(null, "Net", HttpStatusCode.BadRequest, """{"error":{"code":190,"message":"m"}}""", idempotent: false)
            .ErrorCategory.Should().Be(StepRunErrorCategory.Validation);

    [Fact]
    public void A_server_error_with_a_graph_code_stays_an_unknown_outcome()
        => PostOutcome.FromResponse(null, "Net", HttpStatusCode.InternalServerError, """{"error":{"code":190}}""", idempotent: false, graph: true)
            .ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);

    [Fact]
    public void The_failure_reads_as_the_networks_own_words()
        => PostOutcome.FromResponse(null, "Net", HttpStatusCode.UnprocessableEntity, """{"error":"Validation failed: Text character limit of 500 exceeded"}""", idempotent: true)
            .Exception!.Message.Should().Be("Validation failed: Text character limit of 500 exceeded");
}
