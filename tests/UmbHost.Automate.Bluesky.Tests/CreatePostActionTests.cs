using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Tests;

public class CreatePostActionTests
{
    private const string Pds = "https://bsky.social/xrpc/";

    private static readonly BlueskyConnectionSettings Conn = new()
    {
        ServiceUrl = "https://bsky.social",
        Identifier = "umbhost.net",
        AppPassword = "app-pass",
    };

    private const string SessionJson =
        """{"accessJwt":"jwt","refreshJwt":"r","handle":"umbhost.net","did":"did:plc:abc"}""";

    private static Mock<HttpMessageHandler> HandlerWithSession()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(SessionJson));
        return handler;
    }

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
    {
        var factory = handler.CreateClientFactory();
        return new CreatePostAction(ActionTestHarness.Infrastructure, factory, SocialTestHarness.Options());
    }

    [Fact]
    public async Task Posts_text_with_link_facet_and_returns_public_url()
    {
        var handler = HandlerWithSession();
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   recordBody = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                   """{"uri":"at://did:plc:abc/app.bsky.feed.post/3kxyz","cid":"bafy"}""") };
               });

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "New post https://umbhost.net/blog" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePostOutput>().Subject;
        output.Uri.Should().Be("at://did:plc:abc/app.bsky.feed.post/3kxyz");
        output.PostUrl.Should().Be("https://bsky.app/profile/umbhost.net/post/3kxyz");

        using var doc = JsonDocument.Parse(recordBody!);
        doc.RootElement.GetProperty("repo").GetString().Should().Be("did:plc:abc");
        var record = doc.RootElement.GetProperty("record");
        record.GetProperty("$type").GetString().Should().Be("app.bsky.feed.post");
        var facet = record.GetProperty("facets")[0];
        facet.GetProperty("index").GetProperty("byteStart").GetInt32().Should().Be(9);
        facet.GetProperty("index").GetProperty("byteEnd").GetInt32().Should().Be(33);
        facet.GetProperty("features")[0].GetProperty("uri").GetString().Should().Be("https://umbhost.net/blog");
    }

    [Fact]
    public async Task Uploads_image_and_embeds_it_with_alt_text()
    {
        var handler = HandlerWithSession();
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/pic.png")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), configure: r =>
                   r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.uploadBlob")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                   """{"blob":{"$type":"blob","ref":{"$link":"bafkblob"},"mimeType":"image/png","size":3}}"""));
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   recordBody = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                   """{"uri":"at://did:plc:abc/app.bsky.feed.post/3k","cid":"c"}""") };
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = "Look",
            ImageUrl = "https://cdn.example.com/pic.png",
            ImageAltText = "A server rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(recordBody!);
        var image = doc.RootElement.GetProperty("record").GetProperty("embed").GetProperty("images")[0];
        image.GetProperty("alt").GetString().Should().Be("A server rack");
        image.GetProperty("image").GetProperty("ref").GetProperty("$link").GetString().Should().Be("bafkblob");
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Capture(Action<string> body)
        => async (r, _) =>
        {
            body(await r.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uri":"at://did:plc:abc/app.bsky.feed.post/3k","cid":"c"}""") };
        };

    [Fact]
    public async Task A_link_becomes_a_card_with_its_thumbnail()
    {
        var handler = HandlerWithSession();
        handler.SetupRequest(HttpMethod.Get, "https://umbhost.net/card.jpg")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2]), configure: r =>
                   r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg"));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.uploadBlob")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"blob":{"$type":"blob","ref":{"$link":"bafkthumb"},"mimeType":"image/jpeg","size":2}}"""));
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord").Returns(Capture(b => recordBody = b));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = "Read https://umbhost.net/blog",
            LinkUrl = "https://umbhost.net/blog",
            LinkTitle = "Our blog",
            LinkDescription = "News from UmbHost",
            LinkImageUrl = "https://umbhost.net/card.jpg",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(recordBody!);
        var embed = doc.RootElement.GetProperty("record").GetProperty("embed");
        embed.GetProperty("$type").GetString().Should().Be("app.bsky.embed.external");
        var external = embed.GetProperty("external");
        external.GetProperty("uri").GetString().Should().Be("https://umbhost.net/blog");
        external.GetProperty("title").GetString().Should().Be("Our blog");
        external.GetProperty("description").GetString().Should().Be("News from UmbHost");
        external.GetProperty("thumb").GetProperty("ref").GetProperty("$link").GetString().Should().Be("bafkthumb");
    }

    [Fact]
    public async Task A_card_still_goes_out_when_its_thumbnail_cannot_be_fetched()
    {
        var handler = HandlerWithSession();
        handler.SetupRequest(HttpMethod.Get, "https://umbhost.net/missing.jpg").ReturnsResponse(HttpStatusCode.NotFound);
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord").Returns(Capture(b => recordBody = b));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = "Read https://umbhost.net/blog",
            LinkUrl = "https://umbhost.net/blog",
            LinkImageUrl = "https://umbhost.net/missing.jpg",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(recordBody!);
        var external = doc.RootElement.GetProperty("record").GetProperty("embed").GetProperty("external");
        external.GetProperty("title").GetString().Should().Be("");
        external.TryGetProperty("thumb", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_image_takes_the_place_of_the_card()
    {
        var handler = HandlerWithSession();
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/pic.png")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1]), configure: r =>
                   r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.uploadBlob")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"blob":{"$type":"blob","ref":{"$link":"bafkimg"},"mimeType":"image/png","size":1}}"""));
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord").Returns(Capture(b => recordBody = b));

        await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = "Read https://umbhost.net/blog",
            ImageUrl = "https://cdn.example.com/pic.png",
            LinkUrl = "https://umbhost.net/blog",
            LinkTitle = "Our blog",
        }, Conn), default);

        using var doc = JsonDocument.Parse(recordBody!);
        doc.RootElement.GetProperty("record").GetProperty("embed").GetProperty("$type").GetString().Should().Be("app.bsky.embed.images");
    }

    [Fact]
    public async Task A_link_that_is_not_https_gets_no_card()
    {
        var handler = HandlerWithSession();
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord").Returns(Capture(b => recordBody = b));

        await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Read", LinkUrl = "javascript:alert(1)", LinkTitle = "x" }, Conn), default);

        using var doc = JsonDocument.Parse(recordBody!);
        doc.RootElement.GetProperty("record").TryGetProperty("embed", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Rejects_text_over_300_graphemes_without_calling_the_network()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = new string('a', 301) }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task Bad_app_password_is_an_authentication_failure()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent("""{"error":"AuthenticationRequired"}"""));

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "hi" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }

    [Fact]
    public async Task Server_error_on_create_is_not_retried()
    {
        var handler = HandlerWithSession();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .ReturnsResponse(HttpStatusCode.BadGateway);

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "hi" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
    }

    [Fact]
    public async Task Missing_connection_is_an_authentication_failure()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(
            ActionTestHarness.Context(new CreatePostSettings { Text = "hi" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }
}
