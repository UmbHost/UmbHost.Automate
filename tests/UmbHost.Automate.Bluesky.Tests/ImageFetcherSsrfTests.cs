using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// Penetration test: the providers' other URL fetch, the image (and link card thumbnail) download, goes through Umbraco Automate's
/// protected client with Automate's own SSRF handler as its primary handler, as Automate registers it on a site. Every probe into the
/// server's network is refused at connect time, before any connection is made; a non-https URL is refused before a request.
/// </summary>
public class ImageFetcherSsrfTests
{
    private static ImageFetcher Fetcher()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ImageFetcher.ProtectedClientName).ConfigurePrimaryHttpMessageHandler(AutomateSsrfHandler);
        var provider = services.BuildServiceProvider();
        return new ImageFetcher(provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>());
    }

    /// <summary>
    /// Automate's SSRF handler (internal to Umbraco.Automate.Core), made as Automate makes it. Its factory takes no arguments in the lowest
    /// Automate this package supports (17.4) and (allowProxy, allowAutoRedirect) later: no proxy, redirects followed and each hop checked.
    /// </summary>
    private static HttpMessageHandler AutomateSsrfHandler()
    {
        var create = typeof(Umbraco.Automate.Core.Actions.ActionContext).Assembly.GetType("Umbraco.Automate.Core.Security.SsrfProtectionHandler", throwOnError: true)!
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Single(m => m.Name == "Create");
        object?[] arguments = create.GetParameters().Length == 0 ? [] : [false, true];
        return (HttpMessageHandler)(create.Invoke(null, arguments) ?? throw new InvalidOperationException("No SSRF handler."));
    }

    [Theory]
    [InlineData("https://127.0.0.1/a.png")]
    [InlineData("https://127.0.0.1:8443/a.png")] // a non-default port
    [InlineData("https://localhost/a.png")] // a DNS name that resolves to loopback
    [InlineData("https://[::1]/a.png")]
    [InlineData("https://169.254.169.254/latest/meta-data/")] // cloud metadata
    [InlineData("https://10.0.0.1/a.png")]
    [InlineData("https://192.168.1.1/a.png")]
    [InlineData("https://[::ffff:127.0.0.1]/a.png")]
    public async Task A_probe_into_the_network_is_refused_at_connect_time(string url)
    {
        var fetcher = Fetcher();
        fetcher.IsProtected.Should().BeTrue();

        var fetch = () => fetcher.FetchAsync(url, 1_000_000, default);

        var thrown = await fetch.Should().ThrowAsync<HttpRequestException>();
        Chain(thrown.Which).Should().Contain(e => e.GetType().Name == "SsrfException", "Automate's SSRF handler refused the address");
    }

    [Theory]
    [InlineData("http://127.0.0.1/a.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://10.0.0.1/a.png")]
    public async Task A_url_that_is_not_https_is_refused_before_any_request(string url)
    {
        var (image, error) = await Fetcher().FetchAsync(url, 1_000_000, default);

        image.Should().BeNull();
        error.Should().Contain("https");
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
