using System.Net.Http.Headers;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.GitHub.Connection;

/// <summary>
/// GitHub connection type using OAuth via OpenIddict WebIntegration. Validates by calling
/// <c>GET https://api.github.com/user</c> with the resolved access token.
/// </summary>
[ConnectionType("github", "GitHub",
    Group = "Development",
    Icon = "icon-git",
    Description = "Connect to GitHub")]
public sealed class GitHubConnectionType : OAuthConnectionTypeBase<GitHubConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubConnectionType(
        ConnectionTypeInfrastructure infrastructure,
        IOAuthCredentialsService credentialsService,
        IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override string ProviderName => "GitHub";

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var baseResult = await base.ValidateAsync(settings, cancellationToken);
        if (baseResult.Status != ConnectionValidationStatus.Success)
        {
            return baseResult;
        }

        var credentialsId = ((GitHubConnectionSettings)settings!).OAuthCredentialsId!.Value;
        var token = await CredentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UmbHost.Automate.GitHub");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await client.GetAsync("https://api.github.com/user", cancellationToken);
            return response.IsSuccessStatusCode
                ? ConnectionValidationResult.Success("Connected to GitHub.")
                : ConnectionValidationResult.Failure($"GitHub rejected the token ({(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the GitHub API.", [ex.Message]);
        }
    }
}
