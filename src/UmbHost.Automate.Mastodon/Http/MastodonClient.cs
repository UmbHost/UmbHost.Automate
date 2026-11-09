using System.Net.Http.Headers;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Http;

/// <summary>
/// Builds clients pointed at a Mastodon instance's <c>/api/</c> root.
/// </summary>
internal static class MastodonClient
{
    public static HttpClient Create(IHttpClientFactory factory, MastodonConnectionSettings settings)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri(settings.InstanceUrl.TrimEnd('/') + "/api/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        return client;
    }
}
