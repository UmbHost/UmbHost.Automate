using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Tests;

public class CreateFacebookPostActionTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";

    private static readonly MetaConnectionSettings Conn = new() { PageId = "123", PageAccessToken = "ptok" };

    private static CreateFacebookPostAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory());

    [Fact]
    public async Task Text_and_link_go_to_the_page_feed()
    {
        var handler = new Mock<HttpMessageHandler>();
        string? form = null;
        handler.SetupRequest(HttpMethod.Post, Graph + "123/feed")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   r.Headers.Authorization!.Parameter.Should().Be("ptok");
                   form = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"123_456"}""") };
               });
        handler.SetupRequest(HttpMethod.Get, Graph + "123_456?fields=permalink_url")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"permalink_url":"https://www.facebook.com/123/posts/456","id":"123_456"}"""));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings { Message = "Hello", LinkUrl = "https://umbhost.net" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<MetaPostOutput>().Subject;
        output.PostId.Should().Be("123_456");
        output.PostUrl.Should().Be("https://www.facebook.com/123/posts/456");
        form.Should().Contain("message=Hello").And.Contain("link=https%3A%2F%2Fumbhost.net");
    }

    [Fact]
    public async Task Image_goes_to_page_photos_as_a_caption()
    {
        var handler = new Mock<HttpMessageHandler>();
        string? form = null;
        handler.SetupRequest(HttpMethod.Post, Graph + "123/photos")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   form = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"9","post_id":"123_789"}""") };
               });
        handler.SetupRequest(HttpMethod.Get, Graph + "123_789?fields=permalink_url")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"permalink_url":"https://fb/p"}"""));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings { Message = "Pic", ImageUrl = "https://cdn.example.com/a.jpg" }, Conn), default);

        result.OutputData.Should().BeOfType<MetaPostOutput>().Which.PostId.Should().Be("123_789");
        form.Should().Contain("caption=Pic").And.Contain("url=https%3A%2F%2Fcdn.example.com%2Fa.jpg");
    }

    [Fact]
    public async Task Permalink_lookup_failure_still_succeeds_because_the_post_exists()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "123/feed")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"id":"123_456"}"""));
        handler.SetupRequest(HttpMethod.Get, Graph + "123_456?fields=permalink_url")
               .ReturnsResponse(HttpStatusCode.InternalServerError);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings { Message = "Hello" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<MetaPostOutput>().Which.PostUrl.Should().BeEmpty();
    }

    [Fact]
    public async Task Server_error_on_create_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "123/feed").ReturnsResponse(HttpStatusCode.InternalServerError);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings { Message = "Hello" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }

    [Fact]
    public async Task Needs_message_link_or_image()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings(), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }
}

public class CreateInstagramPostActionTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";

    private static readonly MetaConnectionSettings Conn = new()
    {
        PageId = "123",
        PageAccessToken = "ptok",
        InstagramAccountId = "1784",
    };

    private static CreateInstagramPostAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory()) { ContainerPollDelay = TimeSpan.Zero };

    [Fact]
    public async Task Creates_container_waits_until_finished_publishes_and_returns_permalink()
    {
        var handler = new Mock<HttpMessageHandler>();
        string? containerForm = null;
        handler.SetupRequest(HttpMethod.Post, Graph + "1784/media")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   containerForm = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"c1"}""") };
               });
        handler.SetupRequestSequence(HttpMethod.Get, Graph + "c1?fields=status_code")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status_code":"IN_PROGRESS","id":"c1"}"""))
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status_code":"FINISHED","id":"c1"}"""));
        handler.SetupRequest(HttpMethod.Post, Graph + "1784/media_publish")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"id":"m1"}"""));
        handler.SetupRequest(HttpMethod.Get, Graph + "m1?fields=permalink")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"permalink":"https://www.instagram.com/p/abc/"}"""));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateInstagramPostSettings
        {
            Caption = "New",
            ImageUrl = "https://cdn.example.com/a.jpg",
            ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<MetaPostOutput>().Subject;
        output.PostId.Should().Be("m1");
        output.PostUrl.Should().Be("https://www.instagram.com/p/abc/");
        containerForm.Should().Contain("image_url=").And.Contain("caption=New").And.Contain("alt_text=Rack");
    }

    [Fact]
    public async Task Container_error_fails_before_anything_is_published()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Graph + "1784/media")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"id":"c1"}"""));
        handler.SetupRequest(HttpMethod.Get, Graph + "c1?fields=status_code")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status_code":"ERROR"}"""));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateInstagramPostSettings { Caption = "x", ImageUrl = "https://cdn.example.com/a.jpg" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyRequest(HttpMethod.Post, Graph + "1784/media_publish", Times.Never());
    }

    [Fact]
    public async Task Requires_an_image()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateInstagramPostSettings { Caption = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Requires_an_instagram_account_on_the_connection()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateInstagramPostSettings { Caption = "x", ImageUrl = "https://cdn.example.com/a.jpg" },
            new MetaConnectionSettings { PageId = "123", PageAccessToken = "ptok" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }
}
