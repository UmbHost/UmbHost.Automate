using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

public class FirstCommentTests
{
    private const string Rest = "https://api.linkedin.com/rest/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static CreateOrganizationPostAction Sut(Mock<HttpMessageHandler> handler)
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new CreateOrganizationPostAction(
            ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object);
    }

    private static Mock<HttpMessageHandler> PostCreated(List<string>? bodies = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            bodies?.Add(await r.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Add("x-restli-id", "urn:li:share:7001");
            return response;
        });
        return handler;
    }

    private static CreateOrganizationPostSettings Settings(string? comment) => new()
    {
        OrganizationId = "2414183",
        Text = "Hello",
        FirstComment = comment,
    };

    [Fact]
    public async Task Linkedin_posts_then_comments_as_the_author()
    {
        var handler = PostCreated();
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Rest + "socialActions/urn%3Ali%3Ashare%3A7001/comments")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.Created);
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Settings("Read more https://umbhost.net"), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateOrganizationPostOutput>().Which.Warning.Should().BeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("actor").GetString().Should().Be("urn:li:organization:2414183");
        doc.RootElement.GetProperty("object").GetString().Should().Be("urn:li:share:7001");
        doc.RootElement.GetProperty("message").GetProperty("text").GetString().Should().Be("Read more https://umbhost.net");
    }

    [Fact]
    public async Task Linkedin_comment_failure_keeps_the_post_published_with_a_warning()
    {
        var handler = PostCreated();
        handler.SetupRequest(HttpMethod.Post, Rest + "socialActions/urn%3Ali%3Ashare%3A7001/comments")
               .ReturnsResponse(HttpStatusCode.Forbidden, new StringContent("nope"));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Settings("c"), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateOrganizationPostOutput>().Subject;
        output.PostUrl.Should().Be("https://www.linkedin.com/feed/update/urn:li:share:7001/");
        output.Warning.Should().Contain("first comment could not be added").And.Contain("403");
    }

    [Fact]
    public async Task Linkedin_unconfirmed_comment_says_it_may_not_have_been_added()
    {
        var handler = PostCreated();
        handler.SetupRequest(HttpMethod.Post, Rest + "socialActions/urn%3Ali%3Ashare%3A7001/comments")
               .ReturnsResponse(HttpStatusCode.BadGateway, new StringContent("oops"));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Settings("c"), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateOrganizationPostOutput>().Which.Warning.Should().Contain("may not have been added");
    }

    [Fact]
    public async Task Without_the_new_inputs_nothing_changes()
    {
        var bodies = new List<string>();
        var handler = PostCreated(bodies);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Settings(null), Conn), default);

        bodies.Single().Should().Be(
            """{"author":"urn:li:organization:2414183","commentary":"Hello","visibility":"PUBLIC","distribution":{"feedDistribution":"MAIN_FEED","targetEntities":[],"thirdPartyDistributionChannels":[]},"lifecycleState":"PUBLISHED","isReshareDisabledByAuthor":false}""");

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateOrganizationPostOutput>().Which.Warning.Should().BeNull();
        handler.VerifyRequest(HttpMethod.Post, r => r.RequestUri!.AbsolutePath.Contains("socialActions"), Times.Never());
    }
}
