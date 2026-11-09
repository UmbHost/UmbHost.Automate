using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using UmbHost.Automate.Meta.Http;

namespace UmbHost.Automate.Meta.Connection;

/// <summary>
/// Meta connection type for a Facebook Page and its linked Instagram account. Validates the Page
/// token and reports the linked Instagram account id so it can be copied into the connection.
/// </summary>
[ConnectionType("meta", "Facebook & Instagram",
    Description = "Connect to a Facebook Page (and its linked Instagram account) using a Page access token",
    Group = "Social",
    Icon = "icon-facebook")]
public sealed class MetaConnectionType : ConnectionTypeBase<MetaConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MetaConnectionType(ConnectionTypeInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not MetaConnectionSettings typed
            || string.IsNullOrWhiteSpace(typed.PageId)
            || string.IsNullOrWhiteSpace(typed.PageAccessToken))
        {
            return ConnectionValidationResult.Failure("A Page ID and Page access token are required.");
        }

        using var client = GraphClient.Create(_httpClientFactory, typed);
        try
        {
            using var response = await client.GetAsync($"{typed.PageId}?fields=name,instagram_business_account", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"Meta rejected the token or Page ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : typed.PageId;
            var instagram = doc.RootElement.TryGetProperty("instagram_business_account", out var ig)
                ? ig.GetProperty("id").GetString()
                : null;

            if (instagram is null)
            {
                return ConnectionValidationResult.Success($"Connected to the Page '{name}'. No Instagram account is linked to it.");
            }

            return instagram == typed.InstagramAccountId
                ? ConnectionValidationResult.Success($"Connected to the Page '{name}' and its Instagram account.")
                : ConnectionValidationResult.Success(
                    $"Connected to the Page '{name}'. Its linked Instagram account id is {instagram}; enter it to post to Instagram.");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the Graph API.", [ex.Message]);
        }
    }
}
