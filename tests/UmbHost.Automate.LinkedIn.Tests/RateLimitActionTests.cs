using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// LinkedIn documents no reset header (https://learn.microsoft.com/en-us/linkedin/shared/api-guide/concepts/rate-limits), so only the
/// standard Retry-After gives a rate-limited post its reset time.
/// </summary>
public class RateLimitActionTests
{
    private const string Rest = "https://api.linkedin.com/rest/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static Task<ActionResult> Run(HttpResponseMessage answer)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").ReturnsAsync(answer);
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new CreateOrganizationPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object)
            .ExecuteAsync(ActionTestHarness.Context(new CreateOrganizationPostSettings { OrganizationId = "2414183", Text = "Hello" }, Conn), default);
    }

    [Fact]
    public async Task A_rate_limited_post_with_retry_after_says_when_the_limit_resets()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"message":"Resource level throttle limit exceeded.","status":429}"""),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "Fri, 01 Jan 2100 00:00:00 GMT");

        var result = await Run(response);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Resource level throttle limit exceeded.", """UMBSOCIAL-RETRY-AT {"retryAtUtc":"2100-01-01T00:00:00Z"}""");
    }

    [Fact]
    public async Task A_rate_limited_post_without_retry_after_has_no_line()
    {
        var result = await Run(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"message":"Resource level throttle limit exceeded.","status":429}"""),
        });

        result.Exception!.Message.Should().Be("Resource level throttle limit exceeded.");
    }
}
