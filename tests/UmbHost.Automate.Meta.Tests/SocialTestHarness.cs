using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Moq;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// Builds the shared <see cref="ImageFetcher"/> (and the HTTP client options actions take) for tests, with Automate's protected client
/// reported as registered (or not).
/// </summary>
internal static class SocialTestHarness
{
    public static ImageFetcher Fetcher(IHttpClientFactory factory, bool protectedClientRegistered = true)
        => new(factory, Options(protectedClientRegistered));

    public static IOptionsMonitor<HttpClientFactoryOptions> Options(bool protectedClientRegistered = true)
    {
        var options = new HttpClientFactoryOptions();
        if (protectedClientRegistered)
        {
            options.HttpMessageHandlerBuilderActions.Add(_ => { });
        }

        var monitor = new Mock<IOptionsMonitor<HttpClientFactoryOptions>>();
        monitor.Setup(m => m.Get(ImageFetcher.ProtectedClientName)).Returns(options);
        monitor.Setup(m => m.Get(It.Is<string>(n => n != ImageFetcher.ProtectedClientName))).Returns(new HttpClientFactoryOptions());
        return monitor.Object;
    }
}
