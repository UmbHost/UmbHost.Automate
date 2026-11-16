using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Http;

/// <summary>
/// Builds clients pointed at a Mastodon instance's <c>/api/</c> root.
/// </summary>
internal static class MastodonClient
{
    /// <summary>The named client for unauthenticated reads, which never follows a redirect.</summary>
    internal const string PublicClientName = "UmbHost.Automate.Mastodon.Public";

    /// <summary>
    /// Registers <see cref="PublicClientName"/>. A read answered with a redirect is reported, not followed, so a moved or
    /// misconfigured instance can't send the read to another host.
    /// </summary>
    public static void AddPublicClient(IServiceCollection services)
        => services.AddHttpClient(PublicClientName)
                   .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    public static HttpClient Create(IHttpClientFactory factory, MastodonConnectionSettings settings)
    {
        var client = factory.CreateClient();
        client.BaseAddress = ApiRoot(settings);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        return client;
    }

    /// <summary>
    /// A client that sends no token and follows no redirect, for public reads (public statuses need none since 2.7.0:
    /// https://docs.joinmastodon.org/methods/statuses/#get).
    /// </summary>
    public static HttpClient CreatePublic(IHttpClientFactory factory, MastodonConnectionSettings settings)
    {
        var client = factory.CreateClient(PublicClientName);
        client.BaseAddress = ApiRoot(settings);
        return client;
    }

    private static Uri ApiRoot(MastodonConnectionSettings settings) => new(settings.InstanceUrl.TrimEnd('/') + "/api/");
}
