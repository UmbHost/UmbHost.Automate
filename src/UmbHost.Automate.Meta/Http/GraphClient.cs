using System.Net.Http.Headers;
using System.Text.Json;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Http;

/// <summary>
/// Calls the Graph API with the connection's Page access token.
/// </summary>
internal static class GraphClient
{
    public static HttpClient Create(IHttpClientFactory factory, MetaConnectionSettings settings)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri($"https://graph.facebook.com/{settings.GraphVersion.Trim('/')}/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.PageAccessToken);
        return client;
    }

    public static FormUrlEncodedContent Form(params (string Key, string? Value)[] fields)
        => new(fields.Where(f => !string.IsNullOrWhiteSpace(f.Value))
                     .Select(f => new KeyValuePair<string, string>(f.Key, f.Value!)));

    /// <summary>
    /// Reads one string field of an object, e.g. a post's <c>permalink_url</c>. Returns an empty
    /// string on any failure: it is only used after the post exists, where failing would cause a
    /// retry and a duplicate post.
    /// </summary>
    public static async Task<string> TryGetFieldAsync(HttpClient client, string id, string field, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{id}?fields={field}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty(field, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }
}
