using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GoogleBusiness.Configuration;

namespace UmbHost.Automate.GoogleBusiness.Connection;

/// <summary>
/// Google Business Profile connection type using OAuth via OpenIddict's Google provider (<see cref="GoogleBusinessOAuthComposer"/>).
/// Testing the connection lists the account's locations as "Title (accounts/…/locations/…)", so the location a channel needs can be
/// copied from there.
/// </summary>
[ConnectionType("google-business", "Google Business Profile",
    Group = "Social",
    Icon = "icon-google",
    Description = "Connect to Google Business Profile to post updates to your business locations")]
public sealed class GoogleBusinessConnectionType : OAuthConnectionTypeBase<GoogleBusinessConnectionSettings>
{
    // Accounts (Account Management v1) and their locations (Business Information v1) are paged; a few pages cover any real account
    // without an unbounded loop.
    private const int MaxPages = 5;

    private readonly IHttpClientFactory _httpClientFactory;

    public GoogleBusinessConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
        => _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    public override string ProviderName => GoogleBusinessOAuthComposer.ProviderName;

    /// <inheritdoc />
    public override string? SetupDocsUrl => "https://developers.google.com/my-business/content/prereqs";

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var token = await base.ValidateAsync(settings, cancellationToken);
        if (token.Status != ConnectionValidationStatus.Success || settings is not GoogleBusinessConnectionSettings { OAuthCredentialsId: { } id })
        {
            return token;
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await CredentialsService.GetValidAccessTokenAsync(id, cancellationToken));

        var accounts = new List<string>();
        var failure = await PagesAsync(client, "https://mybusinessaccountmanagement.googleapis.com/v1/accounts?pageSize=20", "accounts",
            account => accounts.Add(account.GetProperty("name").GetString()!), cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var locations = new List<string>();
        foreach (var account in accounts)
        {
            failure = await PagesAsync(client,
                $"https://mybusinessbusinessinformation.googleapis.com/v1/{account}/locations?readMask=name,title&pageSize=100", "locations",
                location => locations.Add($"{location.GetProperty("title").GetString()} ({account}/{location.GetProperty("name").GetString()})"),
                cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        return locations.Count == 0
            ? ConnectionValidationResult.Warning("No locations found: add your business to Google Business Profile first.", null)
            : ConnectionValidationResult.Success("Google Business Profile is connected. Use one of these locations in the channel:\n" + string.Join("\n", locations));
    }

    private static async Task<ConnectionValidationResult?> PagesAsync(
        HttpClient client, string url, string property, Action<JsonElement> each, CancellationToken cancellationToken)
    {
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var response = await client.GetAsync(pageToken is null ? url : $"{url}&pageToken={Uri.EscapeDataString(pageToken)}", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"Google returned {(int)response.StatusCode} listing your {property}.", [body]);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty(property, out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    each(item);
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(pageToken))
            {
                return null;
            }
        }

        return null;
    }
}
