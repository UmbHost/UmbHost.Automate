using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The shared image download. Linked into every social package, so tested once here.
/// </summary>
public class ImageFetcherTests
{
    private static Mock<HttpMessageHandler> Serving(byte[] bytes, string contentType)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent(bytes),
            configure: r => r.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType));
        return handler;
    }

    [Fact]
    public async Task Downloads_an_image()
    {
        var fetcher = SocialTestHarness.Fetcher(Serving([1, 2, 3], "image/jpeg").CreateClientFactory());

        var (image, error) = await fetcher.FetchAsync("https://x.io/a/pic.jpg", 10, default);

        error.Should().BeNull();
        image!.Bytes.Should().Equal(1, 2, 3);
        image.ContentType.Should().Be("image/jpeg");
        image.FileName.Should().Be("pic.jpg");
    }

    [Fact]
    public async Task Refuses_when_automate_has_no_protected_client()
    {
        var handler = Serving([1], "image/png");
        var fetcher = SocialTestHarness.Fetcher(handler.CreateClientFactory(), protectedClientRegistered: false);

        var (image, error) = await fetcher.FetchAsync("https://x.io/a.png", 10, default);

        image.Should().BeNull();
        error.Should().Contain("Upgrade Umbraco Automate");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData("http://x.io/a.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Only_https_urls(string url)
    {
        var fetcher = SocialTestHarness.Fetcher(Serving([1], "image/png").CreateClientFactory());

        (await fetcher.FetchAsync(url, 10, default)).Error.Should().Contain("https");
    }

    [Fact]
    public async Task Rejects_non_images()
    {
        var fetcher = SocialTestHarness.Fetcher(Serving([1], "text/html").CreateClientFactory());

        (await fetcher.FetchAsync("https://x.io/a", 10, default)).Error.Should().Contain("JPEG, PNG");
    }

    [Fact]
    public async Task Rejects_oversized_images()
    {
        var fetcher = SocialTestHarness.Fetcher(Serving(new byte[11], "image/png").CreateClientFactory());

        (await fetcher.FetchAsync("https://x.io/a.png", 10, default)).Error.Should().Contain("limit");
    }
}
