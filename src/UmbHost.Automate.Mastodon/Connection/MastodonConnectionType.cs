using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using UmbHost.Automate.Mastodon.Http;

namespace UmbHost.Automate.Mastodon.Connection;

/// <summary>
/// Mastodon connection type. Validates the token against <c>verify_credentials</c>.
/// </summary>
[ConnectionType("mastodon", "Mastodon",
    Description = "Connect to a Mastodon account using an access token",
    Group = "Social",
    Icon = "icon-chat")]
public sealed class MastodonConnectionType : ConnectionTypeBase<MastodonConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MastodonConnectionType(ConnectionTypeInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not MastodonConnectionSettings typed || string.IsNullOrWhiteSpace(typed.AccessToken))
        {
            return ConnectionValidationResult.Failure("An instance URL and access token are required.");
        }

        if (!Uri.TryCreate(typed.InstanceUrl, UriKind.Absolute, out var instance) || instance.Scheme != Uri.UriSchemeHttps)
        {
            return ConnectionValidationResult.Failure("Instance URL must be an https URL.");
        }

        using var client = MastodonClient.Create(_httpClientFactory, typed);
        try
        {
            using var response = await client.GetAsync("v1/accounts/verify_credentials", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"Mastodon rejected the token ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var account = doc.RootElement.TryGetProperty("acct", out var acct) ? acct.GetString() : null;
            return ConnectionValidationResult.Success($"Connected as @{account}@{instance.Host}.");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the Mastodon instance.", [ex.Message]);
        }
    }
}
