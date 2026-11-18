using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Tests;

/// <summary>A rate-limited Mastodon status carries its reset time, and a thread refused before anything is live names part 1.</summary>
public class RateLimitActionTests
{
    private const string Statuses = "https://mastodon.social/api/v1/statuses";
    private static readonly MastodonConnectionSettings Conn = new() { InstanceUrl = "https://mastodon.social", AccessToken = "tok" };
    private const string RetryLine = """UMBSOCIAL-RETRY-AT {"retryAtUtc":"2100-01-01T00:05:01Z"}""";

    private static CreateStatusAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());

    private static HttpResponseMessage RateLimited()
    {
        // As Mastodon's rack_attack.rb answers a throttled request: X-RateLimit-Reset is iso8601(6).
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":"Too many requests"}""") };
        response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "300");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "2100-01-01T00:05:00.500000Z");
        return response;
    }

    private static Mock<HttpMessageHandler> Serve(Func<int, HttpResponseMessage?> answer)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, Statuses).Returns(() =>
        {
            n++;
            return Task.FromResult(answer(n) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"{{n}}","url":"https://mastodon.social/@u/{{n}}"}"""),
            });
        });
        return handler;
    }

    private static Task<ActionResult> Run(Mock<HttpMessageHandler> handler, string? threadParts = null)
        => Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings { Text = "One", ThreadParts = threadParts }, Conn), default);

    [Fact]
    public async Task A_rate_limited_status_says_when_the_limit_resets()
    {
        var result = await Run(Serve(_ => RateLimited()));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal("Too many requests", RetryLine);
    }

    [Fact]
    public async Task A_thread_rate_limited_on_its_first_part_has_the_reset_then_part_1()
    {
        var result = await Run(Serve(n => n == 1 ? RateLimited() : null), """["Two"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Too many requests", RetryLine, """UMBSOCIAL-THREAD {"failedPart":1,"total":2,"posted":[],"failedPartUnknown":false}""");
    }

    [Fact]
    public async Task A_thread_rate_limited_on_a_later_part_is_a_part_failure_without_the_reset()
    {
        var result = await Run(Serve(n => n == 2 ? RateLimited() : null), """["Two"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        var lines = result.Exception!.Message.Split('\n');
        lines.Should().HaveCount(2);
        lines[1].Should().Be("""UMBSOCIAL-THREAD {"failedPart":2,"total":2,"posted":["https://mastodon.social/@u/1"],"failedPartUnknown":false}""");
    }

    [Fact]
    public async Task A_thread_whose_first_part_is_rejected_names_part_1_and_stays_validation()
    {
        var result = await Run(
            Serve(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"error":"Text too long"}""") }),
            """["Two"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Text too long", """UMBSOCIAL-THREAD {"failedPart":1,"total":2,"posted":[],"failedPartUnknown":false}""");
    }

    [Fact]
    public async Task A_single_status_rejection_has_no_thread_line()
    {
        var result = await Run(Serve(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"error":"Text too long"}""") }));

        result.Exception!.Message.Should().Be("Text too long");
    }

    [Fact]
    public async Task A_thread_whose_first_part_hits_a_server_error_has_no_thread_line()
    {
        var result = await Run(Serve(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null), """["Two"]""");

        // Mastodon honours the idempotency key, so this is retried.
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
        result.Exception!.Message.Should().NotContain("UMBSOCIAL-");
    }
}
