using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Http;

/// <summary>
/// Builds clients for LinkedIn's versioned REST API.
/// </summary>
internal static class LinkedInClient
{
    /// <summary>The OpenID Connect userinfo endpoint (Sign In with LinkedIn using OpenID Connect).</summary>
    public const string UserInfoUrl = "https://api.linkedin.com/v2/userinfo";

    /// <summary>The version header's name.</summary>
    public const string VersionHeader = "LinkedIn-Version";

    /// <summary>
    /// A client for <c>https://api.linkedin.com/rest/</c> that sends <paramref name="apiVersion"/>, or the connector's default when it
    /// is empty (<see cref="LinkedInApiVersion.Effective"/>).
    /// </summary>
    public static HttpClient Create(IHttpClientFactory factory, string accessToken, string? apiVersion)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.linkedin.com/rest/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add(VersionHeader, LinkedInApiVersion.Effective(apiVersion));
        client.DefaultRequestHeaders.Add("X-Restli-Protocol-Version", "2.0.0");
        return client;
    }

    /// <summary>The version a client made by <see cref="Create"/> sends.</summary>
    public static string VersionOf(HttpClient client)
        => client.DefaultRequestHeaders.TryGetValues(VersionHeader, out var values) ? values.First() : LinkedInApiVersion.Default;

    /// <summary>
    /// Who the token belongs to: <c>GET /v2/userinfo</c> returns the member's <c>sub</c> and <c>name</c> with the <c>openid</c> and
    /// <c>profile</c> scopes (https://learn.microsoft.com/en-us/linkedin/consumer/integrations/self-serve/sign-in-with-linkedin-v2). Share on
    /// LinkedIn points there for the author's Person URN, <c>urn:li:person:{sub}</c>
    /// (https://learn.microsoft.com/en-us/linkedin/consumer/integrations/self-serve/share-on-linkedin). An unversioned v2 call, so it is
    /// sent without the REST client's version headers.
    /// </summary>
    public static async Task<MemberLookup> UserInfoAsync(IHttpClientFactory factory, string accessToken, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new MemberLookup(null, null, response.StatusCode, body);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var sub = root.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return new MemberLookup(string.IsNullOrWhiteSpace(sub) ? null : sub, name, response.StatusCode, body);
        }
        catch (JsonException)
        {
            return new MemberLookup(null, null, response.StatusCode, body);
        }
    }
}

/// <summary>The member a token belongs to: their <c>sub</c> (null when LinkedIn didn't say) and name, with the response's status and body.</summary>
internal sealed record MemberLookup(string? Sub, string? Name, HttpStatusCode Status, string Body)
{
    /// <summary>The member's Person URN, the author of their posts and owner of their images.</summary>
    public string? PersonUrn => Sub is null ? null : $"urn:li:person:{Sub}";
}
