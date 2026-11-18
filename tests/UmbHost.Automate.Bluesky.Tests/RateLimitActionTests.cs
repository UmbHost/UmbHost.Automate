using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>A rate-limited Bluesky post carries its reset time, and a thread refused before anything is live names part 1.</summary>
public class RateLimitActionTests
{
    private const string Pds = "https://bsky.social/xrpc/";
    private const string Session = """{"accessJwt":"jwt","refreshJwt":"r","handle":"umbhost.net","did":"did:plc:abc"}""";
    private static readonly BlueskyConnectionSettings Conn = new() { ServiceUrl = "https://bsky.social", Identifier = "umbhost.net", AppPassword = "p" };

    // 2100-01-01T00:00:00Z.
    private const string ResetEpoch = "4102444800";
    private const string RetryLine = """UMBSOCIAL-RETRY-AT {"retryAtUtc":"2100-01-01T00:00:00Z"}""";
    private const string PartOneOfThree = """UMBSOCIAL-THREAD {"failedPart":1,"total":3,"posted":[],"failedPartUnknown":false}""";

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());

    private static HttpResponseMessage RateLimited()
    {
        // What the PDS sends on a 429 (bluesky-social/atproto, packages/xrpc-server/src/rate-limiter-http.ts).
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"RateLimitExceeded","message":"Rate Limit Exceeded"}"""),
        };
        response.Headers.TryAddWithoutValidation("RateLimit-Limit", "5000");
        response.Headers.TryAddWithoutValidation("RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("RateLimit-Reset", ResetEpoch);
        response.Headers.TryAddWithoutValidation("RateLimit-Policy", "5000;w=3600");
        return response;
    }

    private static Mock<HttpMessageHandler> Serve(Func<HttpResponseMessage>? session = null, Func<HttpResponseMessage>? record = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .Returns(() => Task.FromResult(session?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Session) }));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .Returns(() => Task.FromResult(record?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StringContent("""{"uri":"at://did:plc:abc/app.bsky.feed.post/k1","cid":"cid1"}"""),
               }));
        return handler;
    }

    private static Task<ActionResult> Run(Mock<HttpMessageHandler> handler, string? threadParts = null)
        => Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "One", ThreadParts = threadParts }, Conn), default);

    [Fact]
    public async Task A_rate_limited_post_says_when_the_limit_resets()
    {
        var result = await Run(Serve(record: RateLimited));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception.Should().BeOfType<HttpRequestException>();
        result.Exception!.Message.Split('\n').Should().Equal("Rate Limit Exceeded", RetryLine);
    }

    [Fact]
    public async Task A_rate_limited_sign_in_says_when_the_limit_resets()
    {
        var result = await Run(Serve(session: RateLimited));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal("Rate Limit Exceeded", RetryLine);
    }

    [Fact]
    public async Task A_thread_rate_limited_on_its_first_part_has_the_reset_then_part_1()
    {
        var result = await Run(Serve(record: RateLimited), """["Two","Three"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal("Rate Limit Exceeded", RetryLine, PartOneOfThree);
    }

    [Fact]
    public async Task A_thread_whose_sign_in_is_refused_names_part_1_and_stays_authentication()
    {
        var result = await Run(
            Serve(session: () => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"AuthenticationRequired","message":"Invalid identifier or password"}""") }),
            """["Two","Three"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Split('\n').Should().Equal("Invalid identifier or password", PartOneOfThree);
    }

    [Fact]
    public async Task A_thread_whose_first_part_is_rejected_names_part_1_and_stays_validation()
    {
        var result = await Run(
            Serve(record: () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"InvalidRequest","message":"Bad record"}""") }),
            """["Two","Three"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Split('\n').Should().Equal("Bad record", PartOneOfThree);
    }

    [Fact]
    public async Task A_single_post_rejection_has_no_thread_line()
    {
        var result = await Run(Serve(record: () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"message":"Bad record"}""") }));

        result.Exception!.Message.Should().Be("Bad record");
    }

    [Fact]
    public async Task A_thread_whose_first_part_may_have_posted_has_no_thread_line()
    {
        var result = await Run(Serve(record: () => new HttpResponseMessage(HttpStatusCode.BadGateway)), """["Two"]""");

        // Bluesky has no idempotency key, so a 5xx may have posted: terminal, and nothing for UmbSocial to read as "not published".
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().NotContain("UMBSOCIAL-");
    }

    [Fact]
    public async Task A_rate_limit_without_a_reset_has_no_retry_line()
    {
        var result = await Run(Serve(record: () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Should().Be("Bluesky returned 429.");
    }
}
