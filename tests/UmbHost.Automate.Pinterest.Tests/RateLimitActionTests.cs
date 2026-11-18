using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Actions;
using UmbHost.Automate.Pinterest.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Pinterest.Tests;

/// <summary>
/// A rate-limited pin carries the reset time from <c>x-ratelimit-reset</c> (seconds, as https://developers.pinterest.com/docs/reference/rate-limits
/// shows it).
/// </summary>
public class RateLimitActionTests
{
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly PinterestConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static Task<ActionResult> Run(HttpResponseMessage answer)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, "https://api.pinterest.com/v5/pins").ReturnsAsync(answer);
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("pina_tok");
        return new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object)
            .ExecuteAsync(ActionTestHarness.Context(new CreatePinSettings
            {
                BoardId = "549755885175",
                Title = "Autumn menu",
                ImageUrl = "https://example.com/media/menu.jpg",
            }, Conn), default);
    }

    [Fact]
    public async Task A_rate_limited_pin_says_when_the_limit_resets()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"code":8,"message":"You have exceeded your rate limit. Try again later."}"""),
        };
        response.Headers.TryAddWithoutValidation("x-ratelimit-limit", "100, 100;w=1, 1000;w=60");
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "60");
        var before = DateTimeOffset.UtcNow;

        var result = await Run(response);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        var lines = result.Exception!.Message.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be("You have exceeded your rate limit. Try again later.");
        RateLimitReset.ParseTail(result.Exception.Message).Should().BeOnOrAfter(before.AddSeconds(60)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(61));
    }

    [Fact]
    public async Task A_rate_limit_without_headers_has_no_line()
    {
        var result = await Run(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"code":8,"message":"Slow down."}""") });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Should().Be("Slow down.");
    }
}
