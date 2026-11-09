using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

public class CreatePostActionTests
{
    private const string Api = "https://api.x.com/2/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly XConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler, string? token = "tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object);
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(
        HttpStatusCode status, string json, Action<string> capture)
        => async (r, _) =>
        {
            capture(await r.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        };

    [Fact]
    public async Task Posts_text_and_returns_the_post_url()
    {
        var handler = new Mock<HttpMessageHandler>();
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Api + "tweets")
               .Returns(Respond(HttpStatusCode.Created, """{"data":{"id":"1850","text":"Hi"}}""", b => body = b));

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePostOutput>().Subject;
        output.PostId.Should().Be("1850");
        output.PostUrl.Should().Be("https://x.com/i/web/status/1850");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("text").GetString().Should().Be("Hi");
    }

    [Fact]
    public async Task Uploads_image_sets_alt_text_and_attaches_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/a.png")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2]), configure: r =>
                   r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"));
        string? uploadForm = null, altBody = null, postBody = null;
        handler.SetupRequest(HttpMethod.Post, Api + "media/upload")
               .Returns(Respond(HttpStatusCode.OK, """{"data":{"id":"m9","media_key":"3_m9"}}""", b => uploadForm = b));
        handler.SetupRequest(HttpMethod.Post, Api + "media/metadata")
               .Returns(Respond(HttpStatusCode.OK, """{"data":{}}""", b => altBody = b));
        handler.SetupRequest(HttpMethod.Post, Api + "tweets")
               .Returns(Respond(HttpStatusCode.Created, """{"data":{"id":"1"}}""", b => postBody = b));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = "Pic",
            ImageUrl = "https://cdn.example.com/a.png",
            ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        uploadForm.Should().Contain("tweet_image");
        using (var alt = JsonDocument.Parse(altBody!))
        {
            alt.RootElement.GetProperty("id").GetString().Should().Be("m9");
            alt.RootElement.GetProperty("metadata").GetProperty("alt_text").GetProperty("text").GetString().Should().Be("Rack");
        }

        using var post = JsonDocument.Parse(postBody!);
        post.RootElement.GetProperty("media").GetProperty("media_ids")[0].GetString().Should().Be("m9");
    }

    [Fact]
    public async Task Rejected_post_is_a_validation_failure()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "tweets")
               .ReturnsResponse(HttpStatusCode.Forbidden, new StringContent("""{"detail":"duplicate content"}"""));

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Server_error_on_create_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "tweets").ReturnsResponse(HttpStatusCode.ServiceUnavailable);

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }

    [Fact]
    public async Task Expired_token_asks_for_reauthorisation()
    {
        var result = await Sut(new Mock<HttpMessageHandler>(), token: null).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }
}
