using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Umbraco.Automate.Core.Actions;
using Umbraco.Cms.Core.Media;
using Umbraco.Cms.Imaging.ImageSharp.Media;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;
using SixImage = SixLabors.ImageSharp.Image;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The image embed carries app.bsky.embed.defs#aspectRatio from the image's real size, read with Umbraco's image dimension reader, so
/// Bluesky shows a wide image wide instead of letterboxed in a square frame. Without a readable size the post still goes, without it.
/// </summary>
public class AspectRatioTests
{
    private const string Pds = "https://bsky.social/xrpc/";
    private const string ImageUrl = "https://cdn.example.com/pic";

    private static readonly BlueskyConnectionSettings Conn = new()
    {
        ServiceUrl = "https://bsky.social",
        Identifier = "umbhost.net",
        AppPassword = "app-pass",
    };

    private static byte[] Encode(int width, int height, bool png)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        if (png)
        {
            image.SaveAsPng(stream);
        }
        else
        {
            image.SaveAsJpeg(stream);
        }

        return stream.ToArray();
    }

    /// <summary>Posts one image and returns the embedded image object Bluesky received.</summary>
    private static async Task<JsonElement> PostImage(byte[] bytes, string contentType, IImageDimensionExtractor? dimensions)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"accessJwt":"jwt","refreshJwt":"r","handle":"umbhost.net","did":"did:plc:abc"}"""));
        handler.SetupRequest(HttpMethod.Get, ImageUrl)
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent(bytes), configure: r =>
                   r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.uploadBlob")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"blob":{"$type":"blob","ref":{"$link":"bafkblob"},"mimeType":"image/png","size":3}}"""));
        string? recordBody = null;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   recordBody = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"uri":"at://did:plc:abc/app.bsky.feed.post/3k","cid":"c"}""") };
               });

        var sut = new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), dimensions);
        var result = await sut.ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Look", ImageUrl = ImageUrl, ImageAltText = "Alt" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(recordBody!);
        return doc.RootElement.GetProperty("record").GetProperty("embed").GetProperty("images")[0].Clone();
    }

    private static IImageDimensionExtractor Umbraco() => new ImageSharpDimensionExtractor(SixLabors.ImageSharp.Configuration.Default);

    [Theory]
    [InlineData(false, "image/jpeg")]
    [InlineData(true, "image/png")]
    public async Task A_wide_image_carries_its_real_aspect_ratio(bool png, string contentType)
    {
        var image = await PostImage(Encode(1200, 630, png), contentType, Umbraco());

        var ratio = image.GetProperty("aspectRatio");
        ratio.GetProperty("width").GetInt32().Should().Be(1200);
        ratio.GetProperty("height").GetInt32().Should().Be(630);
        image.GetProperty("alt").GetString().Should().Be("Alt");
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_post_without_an_aspect_ratio()
        => (await PostImage([1, 2, 3], "image/png", Umbraco())).TryGetProperty("aspectRatio", out _).Should().BeFalse();

    [Fact]
    public async Task Without_a_dimension_reader_the_post_goes_without_an_aspect_ratio()
        => (await PostImage(Encode(1200, 630, png: true), "image/png", dimensions: null)).TryGetProperty("aspectRatio", out _).Should().BeFalse();

    [Fact]
    public async Task A_reader_that_throws_or_answers_nothing_never_fails_the_post()
    {
        var throwing = new Mock<IImageDimensionExtractor>();
        throwing.Setup(d => d.GetDimensions(It.IsAny<Stream>())).Throws<InvalidOperationException>();
        var noop = new Mock<IImageDimensionExtractor>();
        noop.Setup(d => d.GetDimensions(It.IsAny<Stream>())).Returns((System.Drawing.Size?)null);
        var zero = new Mock<IImageDimensionExtractor>();
        zero.Setup(d => d.GetDimensions(It.IsAny<Stream>())).Returns(new System.Drawing.Size(0, 10));

        foreach (var reader in new[] { throwing, noop, zero })
        {
            (await PostImage(Encode(10, 10, png: true), "image/png", reader.Object)).TryGetProperty("aspectRatio", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_sign_in_refused_with_401_is_an_authentication_failure_and_nothing_is_posted()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent(LiveFixture.Text("error-401-sign-in")));

        var sut = new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());
        var context = ActionTestHarness.Context(new CreatePostSettings { Text = "Hello" }, Conn);
        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Be("Invalid identifier or password");
        context.LogEntries.Should().ContainSingle(e => e.Message.StartsWith("Bluesky sign-in returned 401: "));
        handler.VerifyRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord", Times.Never());
    }
}
