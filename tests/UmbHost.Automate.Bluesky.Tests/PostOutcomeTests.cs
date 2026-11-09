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
        var result = PostOutcome.FromResponse("Net", status, "nope", idempotent: false);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public void FromResponse_server_error_without_idempotency_is_terminal_and_says_it_may_have_posted()
    {
        var result = PostOutcome.FromResponse("Net", HttpStatusCode.BadGateway, "", idempotent: false);

        // ConfigurationError is one of Automate's terminal categories, so the step is not retried.
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public void FromResponse_server_error_with_idempotency_is_retryable()
    {
        var result = PostOutcome.FromResponse("Net", HttpStatusCode.BadGateway, "", idempotent: true);

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
        var result = PostOutcome.FromPreparationResponse("Net", "upload", status, "");

        result.ErrorCategory.Should().Be(expected);
    }
}
