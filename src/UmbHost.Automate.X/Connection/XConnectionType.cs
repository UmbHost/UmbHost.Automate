using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.X.Connection;

/// <summary>
/// X connection type using OAuth 2.0 via OpenIddict WebIntegration. Validates by calling
/// <c>GET /2/users/me</c>.
/// </summary>
[ConnectionType("x", "X (Twitter)",
    Group = "Social",
    Icon = "icon-twitter",
    Description = "Connect to an X account")]
public sealed class XConnectionType : OAuthConnectionTypeBase<XConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public XConnectionType(
        ConnectionTypeInfrastructure infrastructure,
        IOAuthCredentialsService credentialsService,
        IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override string ProviderName => "Twitter";

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var baseResult = await base.ValidateAsync(settings, cancellationToken);
        if (baseResult.Status != ConnectionValidationStatus.Success)
        {
            return baseResult;
        }

        var token = await CredentialsService.GetValidAccessTokenAsync(((XConnectionSettings)settings!).OAuthCredentialsId!.Value, cancellationToken);

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await client.GetAsync("https://api.x.com/2/users/me", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"X rejected the token ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var username = doc.RootElement.GetProperty("data").GetProperty("username").GetString();
            return ConnectionValidationResult.Success($"Connected to X as @{username}.");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the X API.", [ex.Message]);
        }
    }
}
