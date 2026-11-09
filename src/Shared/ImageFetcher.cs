using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace UmbHost.Automate.Social;

/// <summary>
/// An image downloaded for upload to a social network.
/// </summary>
internal sealed record FetchedImage(byte[] Bytes, string ContentType, string FileName);

/// <summary>
/// Downloads the image a post attaches, through Umbraco Automate's SSRF-protected HTTP client so an
/// automation cannot be pointed at an internal address.
/// </summary>
internal sealed class ImageFetcher
{
    /// <summary>
    /// Name of Automate's outbound client, whose primary handler blocks private and reserved ranges.
    /// Automate registers it from 17.4.0 / 18.4.0. The constant is copied rather than referenced
    /// because a const is compiled into the caller: on an Automate build that doesn't register the
    /// client, <c>CreateClient</c> would silently return an unprotected one. <see cref="IsProtected"/>
    /// checks for that instead.
    /// </summary>
    internal const string ProtectedClientName = "UmbracoAutomate";

    private static readonly string[] AllowedTypes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HttpClientFactoryOptions> _clientOptions;

    public ImageFetcher(IHttpClientFactory httpClientFactory, IOptionsMonitor<HttpClientFactoryOptions> clientOptions)
    {
        _httpClientFactory = httpClientFactory;
        _clientOptions = clientOptions;
    }

    /// <summary>
    /// True when Automate has configured its protected client on this site.
    /// </summary>
    public bool IsProtected
        => _clientOptions.Get(ProtectedClientName).HttpMessageHandlerBuilderActions.Count > 0;

    /// <summary>
    /// Downloads <paramref name="url"/>. Returns the image, or an error message explaining why it
    /// can't be used.
    /// </summary>
    public async Task<(FetchedImage? Image, string? Error)> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, $"Image URL '{url}' must be an absolute https URL.");
        }

        if (!IsProtected)
        {
            return (null,
                "Images can't be downloaded: this version of Umbraco Automate doesn't provide its protected " +
                "outbound HTTP client. Upgrade Umbraco Automate to 17.4 / 18.4 or later.");
        }

        using var client = _httpClientFactory.CreateClient(ProtectedClientName);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"Downloading the image returned HTTP {(int)response.StatusCode}.");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
        if (!AllowedTypes.Contains(contentType))
        {
            return (null, $"The image must be JPEG, PNG, GIF or WebP (got '{contentType}').");
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return (null, $"The image is {declared} bytes; the limit is {maxBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return (null, $"The image is larger than the {maxBytes}-byte limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        var fileName = Path.GetFileName(uri.AbsolutePath);
        return (new FetchedImage(buffer.ToArray(), contentType, string.IsNullOrEmpty(fileName) ? "image" : fileName), null);
    }
}
