using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Http;

/// <summary>
/// A signed-in Bluesky session.
/// </summary>
internal sealed record BlueskySession(string AccessJwt, string Did, string Handle);

/// <summary>
/// Calls the account's PDS over XRPC.
/// </summary>
internal static class BlueskyClient
{
    public static HttpClient Create(IHttpClientFactory factory, BlueskyConnectionSettings settings)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri(settings.ServiceUrl.TrimEnd('/') + "/xrpc/");
        return client;
    }

    /// <summary>
    /// Signs in with the app password. Returns the session, or the failed response's status and body.
    /// </summary>
    public static async Task<(BlueskySession? Session, HttpStatusCode Status, string Body)> CreateSessionAsync(
        HttpClient client, BlueskyConnectionSettings settings, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "com.atproto.server.createSession",
            new { identifier = settings.Identifier, password = settings.AppPassword },
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (null, response.StatusCode, body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var session = new BlueskySession(
            root.GetProperty("accessJwt").GetString()!,
            root.GetProperty("did").GetString()!,
            root.GetProperty("handle").GetString()!);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessJwt);
        return (session, response.StatusCode, body);
    }
}
