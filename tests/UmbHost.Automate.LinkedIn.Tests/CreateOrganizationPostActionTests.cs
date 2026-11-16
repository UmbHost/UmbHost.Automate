using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

public class CreateOrganizationPostActionTests
{
    private const string Rest = "https://api.linkedin.com/rest/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static CreateOrganizationPostAction Sut(Mock<HttpMessageHandler> handler, string? token = "tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new CreateOrganizationPostAction(
            ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object)
        {
            ImagePollDelay = TimeSpan.Zero,
        };
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Created(Action<HttpRequestMessage, string> capture)
        => async (r, _) =>
        {
            capture(r, await r.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Add("x-restli-id", "urn:li:share:7001");
            return response;
        };

    [Fact]
    public async Task Posts_as_the_organization_with_versioned_headers_and_escaped_text()
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").Returns(Created((r, b) => (sent, body) = (r, b)));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateOrganizationPostSettings
        {
            OrganizationId = "2414183",
            Text = "New (beta) #Umbraco",
            LinkUrl = "https://umbhost.net/blog",
            LinkTitle = "Blog",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateOrganizationPostOutput>().Subject;
        output.PostUrn.Should().Be("urn:li:share:7001");
        output.PostUrl.Should().Be("https://www.linkedin.com/feed/update/urn:li:share:7001/");

        sent!.Headers.GetValues("LinkedIn-Version").Single().Should().Be("202610");
        sent.Headers.GetValues("X-Restli-Protocol-Version").Single().Should().Be("2.0.0");
        sent.Headers.Authorization!.Parameter.Should().Be("tok");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("author").GetString().Should().Be("urn:li:organization:2414183");
        doc.RootElement.GetProperty("commentary").GetString().Should().Be(@"New \(beta\) #Umbraco");
        doc.RootElement.GetProperty("content").GetProperty("article").GetProperty("title").GetString().Should().Be("Blog");
    }

    [Fact]
    public async Task Uploads_image_waits_until_available_and_attaches_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/a.png")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2]), configure: r =>
                   r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"));
        handler.SetupRequest(HttpMethod.Post, Rest + "images?action=initializeUpload")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                   """{"value":{"uploadUrl":"https://www.linkedin.com/dms-uploads/x","image":"urn:li:image:C4E"}}"""));
        handler.SetupRequest(HttpMethod.Put, "https://www.linkedin.com/dms-uploads/x").ReturnsResponse(HttpStatusCode.Created);
        handler.SetupRequestSequence(HttpMethod.Get, Rest + "images/urn%3Ali%3Aimage%3AC4E")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status":"PROCESSING"}"""))
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status":"AVAILABLE"}"""));
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").Returns(Created((_, b) => body = b));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateOrganizationPostSettings
        {
            OrganizationId = "1",
            Text = "Pic",
            ImageUrl = "https://cdn.example.com/a.png",
            ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(body!);
        var media = doc.RootElement.GetProperty("content").GetProperty("media");
        media.GetProperty("id").GetString().Should().Be("urn:li:image:C4E");
        media.GetProperty("altText").GetString().Should().Be("Rack");
    }

    [Fact]
    public async Task Expired_token_asks_for_reauthorisation()
    {
        var result = await Sut(new Mock<HttpMessageHandler>(), token: null).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "1", Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Contain("Re-authorise");
    }

    [Fact]
    public async Task Link_without_title_is_rejected()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "1", Text = "x", LinkUrl = "https://a.io" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Link_and_image_together_are_rejected()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings
            {
                OrganizationId = "1", Text = "x", LinkUrl = "https://a.io", LinkTitle = "t", ImageUrl = "https://a.io/i.png",
            }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Server_error_on_create_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").ReturnsResponse(HttpStatusCode.InternalServerError);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "1", Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }
}

public class LittleTextTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a (b) [c] {d} <e>", @"a \(b\) \[c\] \{d\} \<e\>")]
    [InlineData("me@umbhost.net", @"me\@umbhost.net")]
    [InlineData("*bold* _x_ ~y~ a|b", @"\*bold\* \_x\_ \~y\~ a\|b")]
    [InlineData(@"C:\path", @"C:\\path")]
    [InlineData("#Umbraco rocks", "#Umbraco rocks")]
    [InlineData("issue # 5", @"issue \# 5")]
    public void Escapes_reserved_characters_but_keeps_hashtags(string input, string expected)
        => LittleText.Escape(input).Should().Be(expected);
}
