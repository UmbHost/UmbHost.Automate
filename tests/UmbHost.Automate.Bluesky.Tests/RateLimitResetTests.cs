using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The shared rate-limit reset reader, its machine line, and the thread's part-1 line. The sources are linked into every social
/// package, so they are tested once here.
/// </summary>
public class RateLimitResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HttpResponseHeaders Headers(params (string Name, string Value)[] values)
    {
        var headers = new HttpResponseMessage().Headers;
        foreach (var (name, value) in values)
        {
            headers.TryAddWithoutValidation(name, value);
        }

        return headers;
    }

    [Fact]
    public void Retry_after_in_seconds()
        => RateLimitReset.Find("LinkedIn", Headers(("Retry-After", "120")), null, Now).Should().Be(Now.AddMinutes(2));

    [Fact]
    public void Retry_after_as_an_http_date()
        => RateLimitReset.Find("Threads", Headers(("Retry-After", "Wed, 18 Nov 2026 14:05:00 GMT")), null, Now)
            .Should().Be(new DateTimeOffset(2026, 11, 18, 14, 5, 0, TimeSpan.Zero));

    [Fact]
    public void Bluesky_ratelimit_reset_is_epoch_seconds()
        => RateLimitReset.Find("Bluesky", Headers(("RateLimit-Reset", Now.AddMinutes(5).ToUnixTimeSeconds().ToString())), null, Now)
            .Should().Be(Now.AddMinutes(5));

    [Fact]
    public void X_rate_limit_reset_is_epoch_seconds()
        => RateLimitReset.Find("X", Headers(("x-rate-limit-reset", Now.AddMinutes(15).ToUnixTimeSeconds().ToString())), null, Now)
            .Should().Be(Now.AddMinutes(15));

    [Fact]
    public void Mastodon_ratelimit_reset_is_iso_8601_with_fractions_rounded_up_to_the_second()
        => RateLimitReset.Find("Mastodon", Headers(("X-RateLimit-Reset", "2026-11-18T14:05:00.250000Z")), null, Now)
            .Should().Be(new DateTimeOffset(2026, 11, 18, 14, 5, 1, TimeSpan.Zero));

    [Fact]
    public void Pinterest_ratelimit_reset_is_seconds_from_now()
        => RateLimitReset.Find("Pinterest", Headers(("x-ratelimit-reset", "60")), null, Now).Should().Be(Now.AddSeconds(60));

    [Fact]
    public void Graph_business_use_case_takes_the_longest_wait_in_minutes()
    {
        const string usage = """
            {"111":[{"type":"pages","call_count":100,"estimated_time_to_regain_access":19}],
             "222":[{"type":"instagram","call_count":100,"estimated_time_to_regain_access":0},{"type":"pages","estimated_time_to_regain_access":25}]}
            """;

        RateLimitReset.Find("Facebook", Headers(("X-Business-Use-Case-Usage", usage)), null, Now).Should().Be(Now.AddMinutes(25));
        RateLimitReset.Find("Instagram", Headers(("X-Business-Use-Case-Usage", usage)), null, Now).Should().Be(Now.AddMinutes(25));
    }

    [Fact]
    public void Graph_app_usage_has_no_time()
        => RateLimitReset.Find("Facebook", Headers(("X-App-Usage", """{"call_count":100,"total_time":25,"total_cputime":25}""")), null, Now)
            .Should().BeNull();

    [Fact]
    public void Google_retry_info_delay_in_the_error_details()
    {
        const string body = """
            {"error":{"code":429,"status":"RESOURCE_EXHAUSTED","message":"Quota exceeded.",
             "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"RATE_LIMIT_EXCEEDED"},
                        {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"30.5s"}]}}
            """;

        RateLimitReset.Find("Google Business Profile", null, body, Now).Should().Be(Now.AddSeconds(31));
    }

    [Fact]
    public void The_latest_of_several_signals_wins()
        => RateLimitReset.Find("Bluesky", Headers(
                ("Retry-After", "60"),
                ("RateLimit-Reset", Now.AddMinutes(10).ToUnixTimeSeconds().ToString())), null, Now)
            .Should().Be(Now.AddMinutes(10));

    [Fact]
    public void A_header_is_read_only_for_the_network_that_documents_it()
    {
        // Pinterest's x-ratelimit-reset is seconds, Mastodon's (the same name) a timestamp; LinkedIn documents neither.
        RateLimitReset.Find("Mastodon", Headers(("X-RateLimit-Reset", "60")), null, Now).Should().BeNull();
        RateLimitReset.Find("Pinterest", Headers(("X-RateLimit-Reset", "2026-11-18T14:05:00Z")), null, Now).Should().BeNull();
        RateLimitReset.Find("LinkedIn", Headers(("x-rate-limit-reset", Now.AddMinutes(5).ToUnixTimeSeconds().ToString())), null, Now).Should().BeNull();
    }

    [Fact]
    public void No_headers_no_time()
    {
        RateLimitReset.Find("Bluesky", null, null, Now).Should().BeNull();
        RateLimitReset.Find("Bluesky", Headers(), "", Now).Should().BeNull();
    }

    [Theory]
    [InlineData("Bluesky", "RateLimit-Reset", "soon")]
    [InlineData("Bluesky", "RateLimit-Reset", "-5")]
    [InlineData("X", "x-rate-limit-reset", "1.5e9")]
    [InlineData("Mastodon", "X-RateLimit-Reset", "next tuesday")]
    [InlineData("Mastodon", "X-RateLimit-Reset", "2026-11-18T14:05:00")]
    [InlineData("Pinterest", "x-ratelimit-reset", "abc")]
    [InlineData("Facebook", "X-Business-Use-Case-Usage", "{not json")]
    [InlineData("Facebook", "X-Business-Use-Case-Usage", """{"1":[{"estimated_time_to_regain_access":"19"}]}""")]
    [InlineData("LinkedIn", "Retry-After", "whenever")]
    public void Garbage_gives_no_time(string network, string header, string value)
        => RateLimitReset.Find(network, Headers((header, value)), null, Now).Should().BeNull();

    [Fact]
    public void Google_garbage_gives_no_time()
    {
        RateLimitReset.Find("Google Business Profile", null, "<html>", Now).Should().BeNull();
        RateLimitReset.Find("Google Business Profile", null, """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"soon"}]}}""", Now)
            .Should().BeNull();
    }

    [Fact]
    public void A_time_in_the_past_is_ignored()
    {
        RateLimitReset.Find("Bluesky", Headers(("RateLimit-Reset", Now.AddMinutes(-1).ToUnixTimeSeconds().ToString())), null, Now).Should().BeNull();
        RateLimitReset.Find("Mastodon", Headers(("X-RateLimit-Reset", "2020-01-01T00:00:00Z")), null, Now).Should().BeNull();
        RateLimitReset.Find("Threads", Headers(("Retry-After", "Tue, 17 Nov 2026 14:05:00 GMT")), null, Now).Should().BeNull();
        RateLimitReset.Find("LinkedIn", Headers(("Retry-After", "0")), null, Now).Should().BeNull();
    }

    [Fact]
    public void The_tail_is_one_exact_line()
        => RateLimitReset.Tail(new DateTimeOffset(2026, 11, 18, 14, 5, 0, TimeSpan.FromHours(1)))
            .Should().Be("\nUMBSOCIAL-RETRY-AT {\"retryAtUtc\":\"2026-11-18T13:05:00Z\"}");

    [Fact]
    public void FromResponse_appends_the_line_to_a_rate_limit_after_the_sentence()
    {
        var result = PostOutcome.FromResponse(null, "LinkedIn", HttpStatusCode.TooManyRequests, """{"message":"Slow down."}""", idempotent: false,
            headers: Headers(("Retry-After", "300")), clock: new FixedClock(Now));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception.Should().BeOfType<HttpRequestException>();
        result.Exception!.Message.Should().Be("Slow down.\nUMBSOCIAL-RETRY-AT {\"retryAtUtc\":\"2026-11-18T14:05:00Z\"}");
        RateLimitReset.ParseTail(result.Exception.Message).Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void FromResponse_graph_throttling_code_gets_the_line_too()
    {
        var result = PostOutcome.FromResponse(null, "Facebook", HttpStatusCode.BadRequest, """{"error":{"message":"Too many calls","code":32}}""",
            idempotent: false, graph: true,
            headers: Headers(("X-Business-Use-Case-Usage", """{"1":[{"type":"pages","estimated_time_to_regain_access":5}]}""")),
            clock: new FixedClock(Now));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Should().Be("Too many calls\nUMBSOCIAL-RETRY-AT {\"retryAtUtc\":\"2026-11-18T14:05:00Z\"}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void FromResponse_adds_no_line_to_anything_but_a_rate_limit(HttpStatusCode status)
    {
        var result = PostOutcome.FromResponse(null, "LinkedIn", status, "", idempotent: true,
            headers: Headers(("Retry-After", "300")), clock: new FixedClock(Now));

        result.Exception!.Message.Should().NotContain(RateLimitReset.TailPrefix);
    }

    [Fact]
    public void FromResponse_rate_limit_without_a_reset_has_no_line()
        => PostOutcome.FromResponse(null, "LinkedIn", HttpStatusCode.TooManyRequests, "", idempotent: false, headers: Headers())
            .Exception!.Message.Should().Be("LinkedIn returned 429.");

    [Fact]
    public void FromPreparationResponse_appends_the_line_to_a_rate_limit()
    {
        var result = PostOutcome.FromPreparationResponse(null, "Bluesky", "sign-in", HttpStatusCode.TooManyRequests, "",
            headers: Headers(("RateLimit-Reset", Now.AddMinutes(5).ToUnixTimeSeconds().ToString())), clock: new FixedClock(Now));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Should().Be("Bluesky sign-in returned 429.\nUMBSOCIAL-RETRY-AT {\"retryAtUtc\":\"2026-11-18T14:05:00Z\"}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void FromPreparationResponse_adds_no_line_to_anything_but_a_rate_limit(HttpStatusCode status)
        => PostOutcome.FromPreparationResponse(null, "Bluesky", "sign-in", status, "",
                headers: Headers(("Retry-After", "300")), clock: new FixedClock(Now))
            .Exception!.Message.Should().NotContain(RateLimitReset.TailPrefix);
}

/// <summary>The machine line a thread refused before anything was live carries.</summary>
public class FirstPartFailedTests
{
    private const string PartOneLine = """UMBSOCIAL-THREAD {"failedPart":1,"total":3,"posted":[],"failedPartUnknown":false}""";

    [Theory]
    [InlineData(StepRunErrorCategory.Validation)]
    [InlineData(StepRunErrorCategory.Authentication)]
    [InlineData(StepRunErrorCategory.RateLimiting)]
    public void A_refusal_keeps_its_category_and_sentence_and_names_part_1(StepRunErrorCategory category)
    {
        var original = new UnauthorizedAccessException("Nope.");

        var result = ThreadParts.FirstPartFailed(ActionResult.Failed(original, category), 3);

        result.ErrorCategory.Should().Be(category);
        result.Exception.Should().BeOfType<UnauthorizedAccessException>().Which.InnerException.Should().BeSameAs(original);
        result.Exception!.Message.Should().Be("Nope.\n" + PartOneLine);
        var tail = ThreadParts.ParseTail(result.Exception.Message)!;
        tail.FailedPart.Should().Be(1);
        tail.Total.Should().Be(3);
        tail.Posted.Should().BeEmpty();
        tail.FailedPartUnknown.Should().BeFalse();
    }

    [Fact]
    public void The_retry_line_comes_before_the_thread_line()
    {
        var limited = PostOutcome.FromResponse(null, "LinkedIn", HttpStatusCode.TooManyRequests, "", idempotent: false,
            headers: WithRetryAfter());

        var lines = ThreadParts.FirstPartFailed(limited, 3).Exception!.Message.Split('\n');

        lines.Should().HaveCount(3);
        lines[0].Should().Be("LinkedIn returned 429.");
        lines[1].Should().StartWith(RateLimitReset.TailPrefix);
        lines[2].Should().Be(PartOneLine);
    }

    [Theory]
    [InlineData(StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(StepRunErrorCategory.Unknown)]
    [InlineData(StepRunErrorCategory.ConfigurationError)]
    [InlineData(StepRunErrorCategory.Timeout)]
    public void Anything_that_may_have_posted_or_is_transient_is_unchanged(StepRunErrorCategory category)
    {
        var failure = ActionResult.Failed(new HttpRequestException("Hmm."), category);

        ThreadParts.FirstPartFailed(failure, 3).Should().BeSameAs(failure);
    }

    [Fact]
    public void No_thread_or_a_message_with_a_line_already_is_unchanged()
    {
        var failure = ActionResult.Failed(new InvalidOperationException("Nope."), StepRunErrorCategory.Validation);
        ThreadParts.FirstPartFailed(failure, 1).Should().BeSameAs(failure);

        var tailed = ActionResult.Failed(new InvalidOperationException("Nope.\n" + PartOneLine), StepRunErrorCategory.Validation);
        ThreadParts.FirstPartFailed(tailed, 3).Should().BeSameAs(tailed);
    }

    private static HttpResponseHeaders WithRetryAfter()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("Retry-After", "60");
        return headers;
    }
}
