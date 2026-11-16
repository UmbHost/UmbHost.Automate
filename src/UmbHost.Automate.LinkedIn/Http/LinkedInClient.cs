using System.Net.Http.Headers;

namespace UmbHost.Automate.LinkedIn.Http;

/// <summary>
/// Builds clients for LinkedIn's versioned REST API.
/// </summary>
internal static class LinkedInClient
{
    public static HttpClient Create(IHttpClientFactory factory, string accessToken, string apiVersion)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.linkedin.com/rest/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("LinkedIn-Version", apiVersion);
        client.DefaultRequestHeaders.Add("X-Restli-Protocol-Version", "2.0.0");
        return client;
    }
}
