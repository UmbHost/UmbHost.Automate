using Umbraco.Automate.Core.Connections;
using UmbHost.Automate.Teamwork.Http;

namespace UmbHost.Automate.Teamwork.Connection;

/// <summary>
/// Teamwork Projects connection type for Umbraco Automate. Stores a site + API token per
/// connection and validates the token against the Teamwork <c>me.json</c> endpoint before saving.
/// </summary>
[ConnectionType("teamwork", "Teamwork Projects",
    Description = "Connect to Teamwork Projects using an API token",
    Group = "Project Management",
    Icon = "icon-checkbox")]
public sealed class TeamworkConnectionType : ConnectionTypeBase<TeamworkConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TeamworkConnectionType(ConnectionTypeInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not TeamworkConnectionSettings typed)
        {
            return ConnectionValidationResult.Failure("Connection settings are missing.");
        }

        if (string.IsNullOrWhiteSpace(typed.Site))
        {
            return ConnectionValidationResult.Failure("Site is required.");
        }

        if (string.IsNullOrWhiteSpace(typed.ApiToken))
        {
            return ConnectionValidationResult.Failure("An API token is required.");
        }

        using var client = TeamworkRequestHelper.CreateClient(_httpClientFactory, typed);
        try
        {
            using var response = await client.GetAsync("me.json", cancellationToken);
            return response.IsSuccessStatusCode
                ? ConnectionValidationResult.Success($"Connected to {typed.Site}.teamwork.com.")
                : ConnectionValidationResult.Failure($"Teamwork rejected the token ({(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the Teamwork API.", [ex.Message]);
        }
    }
}
