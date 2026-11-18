using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GoogleBusiness.Actions;
using UmbHost.Automate.GoogleBusiness.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.GoogleBusiness.Tests;

/// <summary>
/// A rate-limited update carries the reset time from a <c>google.rpc.RetryInfo</c> in the error's details (googleapis
/// google/rpc/error_details.proto), or from Retry-After.
/// </summary>
public class RateLimitActionTests
{
    private const string Posts = "https://mybusiness.googleapis.com/v4/accounts/111/locations/222/localPosts";
    private const string Voice = "https://mybusinessverifications.googleapis.com/v1/locations/222/VoiceOfMerchantState";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly GoogleBusinessConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static Task<ActionResult> Run(Func<HttpResponseMessage> voice, HttpResponseMessage? post = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Voice).ReturnsAsync(voice);
        handler.SetupRequest(HttpMethod.Post, Posts).ReturnsAsync(post ?? new HttpResponseMessage(HttpStatusCode.OK));
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("ya29.tok");
        return new CreateUpdateAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object)
            .ExecuteAsync(ActionTestHarness.Context(new CreateUpdateSettings { LocationName = "accounts/111/locations/222", Summary = "Autumn menu" }, Conn), default);
    }

    private static HttpResponseMessage Verified()
        => new(HttpStatusCode.OK) { Content = new StringContent("""{"hasVoiceOfMerchant":true,"hasBusinessAuthority":true}""") };

    [Fact]
    public async Task A_rate_limited_update_says_when_the_quota_allows_again()
    {
        var before = DateTimeOffset.UtcNow;

        var result = await Run(Verified, new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""
                {"error":{"code":429,"message":"Quota exceeded for quota metric 'Requests'.","status":"RESOURCE_EXHAUSTED",
                 "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"RATE_LIMIT_EXCEEDED","domain":"googleapis.com"},
                            {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"45s"}]}}
                """),
        });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        var lines = result.Exception!.Message.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Be("Quota exceeded for quota metric 'Requests'.");
        RateLimitReset.ParseTail(result.Exception.Message).Should().BeOnOrAfter(before.AddSeconds(45)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(46));
    }

    [Fact]
    public async Task A_rate_limited_verification_check_with_retry_after_says_when()
    {
        var result = await Run(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":429,"message":"Too many requests.","status":"RESOURCE_EXHAUSTED"}}"""),
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "Fri, 01 Jan 2100 00:00:00 GMT");
            return response;
        });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
        result.Exception!.Message.Split('\n').Should().Equal(
            "Too many requests.", """UMBSOCIAL-RETRY-AT {"retryAtUtc":"2100-01-01T00:00:00Z"}""");
    }
}
