using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// A throttled Facebook or Instagram post carries the reset time from <c>X-Business-Use-Case-Usage</c>'s
/// <c>estimated_time_to_regain_access</c> (minutes, https://developers.facebook.com/docs/graph-api/overview/rate-limiting).
/// </summary>
public class RateLimitActionTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";
    private static readonly MetaConnectionSettings Conn = new() { PageId = "123", PageAccessToken = "ptok", InstagramAccountId = "1784" };

    private static HttpResponseMessage Throttled(string type)
    {
        // Graph answers a throttled call with 400 and code 32 (Page) or 4 (app); the status alone isn't 429.
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"User request limit reached","type":"OAuthException","code":32}}"""),
        };
        response.Headers.TryAddWithoutValidation("X-Business-Use-Case-Usage",
            $$$"""{"123":[{"type":"{{{type}}}","call_count":100,"total_cputime":20,"total_time":20,"estimated_time_to_regain_access":30}]}""");
        return response;
    }

    private static void ShouldResetInHalfAnHour(ActionResult result, DateTimeOffset before)
    {
        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        var lines = result.Exception!.Message.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be("User request limit reached");
        lines[1].Should().StartWith(RateLimitReset.TailPrefix);
        RateLimitReset.ParseTail(result.Exception.Message).Should().BeOnOrAfter(before.AddMinutes(30)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddMinutes(30).AddSeconds(1));
    }

    [Fact]
    public async Task A_throttled_facebook_post_says_when_the_limit_resets()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "123/feed").ReturnsAsync(() => Throttled("pages"));
        var before = DateTimeOffset.UtcNow;

        var result = await new CreateFacebookPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory())
            .ExecuteAsync(ActionTestHarness.Context(new CreateFacebookPostSettings { Message = "Hello" }, Conn), default);

        ShouldResetInHalfAnHour(result, before);
    }

    [Fact]
    public async Task A_throttled_instagram_container_says_when_the_limit_resets()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "1784/media").ReturnsAsync(() => Throttled("instagram"));
        var before = DateTimeOffset.UtcNow;

        var result = await new CreateInstagramPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory()) { ContainerPollDelay = TimeSpan.Zero }
            .ExecuteAsync(ActionTestHarness.Context(new CreateInstagramPostSettings { Caption = "New", ImageUrl = "https://cdn.example.com/a.jpg" }, Conn), default);

        ShouldResetInHalfAnHour(result, before);
        handler.VerifyRequest(HttpMethod.Post, Graph + "1784/media_publish", Times.Never());
    }

    [Fact]
    public async Task App_usage_alone_has_no_reset_time()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "123/feed").ReturnsAsync(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"Application request limit reached","code":4}}"""),
            };
            response.Headers.TryAddWithoutValidation("X-App-Usage", """{"call_count":100,"total_time":25,"total_cputime":25}""");
            return response;
        });

        var result = await new CreateFacebookPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory())
            .ExecuteAsync(ActionTestHarness.Context(new CreateFacebookPostSettings { Message = "Hello" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Should().Be("Application request limit reached");
    }
}
