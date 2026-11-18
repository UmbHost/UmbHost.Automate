using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

/// <summary>A rate-limited X post carries its reset time, and a thread refused before anything is live names part 1.</summary>
public class RateLimitActionTests
{
    private const string Api = "https://api.x.com/2/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly XConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    // 2100-01-01T00:00:00Z.
    private const string ResetEpoch = "4102444800";
    private const string RetryLine = """UMBSOCIAL-RETRY-AT {"retryAtUtc":"2100-01-01T00:00:00Z"}""";

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object)
        {
            StatusPollDelay = TimeSpan.Zero,
        };
    }

    private static HttpResponseMessage RateLimited()
    {
        // The headers https://docs.x.com/x-api/fundamentals/rate-limits documents.
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"title":"Too Many Requests","detail":"Too Many Requests"}""") };
        response.Headers.TryAddWithoutValidation("x-rate-limit-limit", "100");
        response.Headers.TryAddWithoutValidation("x-rate-limit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-rate-limit-reset", ResetEpoch);
        return response;
    }

    private static Mock<HttpMessageHandler> Serve(Func<int, HttpResponseMessage?> answer)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, Api + "tweets").Returns(() =>
        {
            n++;
            return Task.FromResult(answer(n) ?? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($$$"""{"data":{"id":"{{{n}}}"}}""") });
        });
        return handler;
    }

    private static Task<ActionResult> Run(Mock<HttpMessageHandler> handler, string? threadParts = null, string? imageUrl = null)
        => Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "One", ThreadParts = threadParts, ImageUrl = imageUrl }, Conn), default);

    [Fact]
    public async Task A_rate_limited_post_says_when_the_limit_resets()
    {
        var result = await Run(Serve(_ => RateLimited()));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal("Too Many Requests", RetryLine);
    }

    [Fact]
    public async Task A_rate_limited_image_upload_in_a_thread_has_the_reset_then_part_1()
    {
        var handler = Serve(_ => null);
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/pic.jpg")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), configure: r =>
                   r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"));
        handler.SetupRequest(HttpMethod.Post, Api + "media/upload").ReturnsAsync(RateLimited);

        var result = await Run(handler, """["Two","Three"]""", "https://cdn.example.com/pic.jpg");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Too Many Requests", RetryLine, """UMBSOCIAL-THREAD {"failedPart":1,"total":3,"posted":[],"failedPartUnknown":false}""");
        handler.VerifyRequest(HttpMethod.Post, Api + "tweets", Times.Never());
    }

    [Fact]
    public async Task A_thread_whose_first_part_is_rejected_names_part_1_and_stays_validation()
    {
        var result = await Run(
            Serve(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"detail":"Duplicate content."}""") }),
            """["Two"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Duplicate content.", """UMBSOCIAL-THREAD {"failedPart":1,"total":2,"posted":[],"failedPartUnknown":false}""");
    }

    [Fact]
    public async Task A_single_post_rejection_has_no_thread_line()
    {
        var result = await Run(Serve(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"detail":"Duplicate content."}""") }));

        result.Exception!.Message.Should().Be("Duplicate content.");
    }

    [Fact]
    public async Task A_thread_whose_first_part_may_have_posted_has_no_thread_line()
    {
        var result = await Run(Serve(n => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null), """["Two"]""");

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().NotContain("UMBSOCIAL-");
    }
}
