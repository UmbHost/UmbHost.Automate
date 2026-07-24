using System.Net.Http.Headers;
using System.Text;
using UmbHost.Automate.Teamwork.Connection;

namespace UmbHost.Automate.Teamwork.Http;

/// <summary>
/// Builds <see cref="HttpClient"/> instances pointed at the Teamwork Projects v3 API and
/// authenticated with the connection's API token (HTTP Basic — token as username).
/// </summary>
public static class TeamworkRequestHelper
{
    /// <summary>
    /// Creates a client with the base address and Basic auth header set from the connection settings.
    /// </summary>
    public static HttpClient CreateClient(IHttpClientFactory factory, TeamworkConnectionSettings settings)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri($"https://{settings.Site}.teamwork.com/projects/api/v3/");
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.ApiToken}:x"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        return client;
    }
}
