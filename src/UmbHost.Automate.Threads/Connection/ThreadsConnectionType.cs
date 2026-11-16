using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Threads.Configuration;

namespace UmbHost.Automate.Threads.Connection;

/// <summary>
/// Threads connection type using OAuth via the OpenIddict client (<see cref="ThreadsOAuthComposer"/>). Validation refreshes the token
/// when it is due (<see cref="ThreadsTokens"/>) and names the account.
/// </summary>
[ConnectionType("threads", "Threads",
    Group = "Social",
    Icon = "icon-umbsocial-threads",
    Description = "Connect to Threads to post as a Threads profile")]
public sealed class ThreadsConnectionType : OAuthConnectionTypeBase<ThreadsConnectionSettings>
{
    private readonly ThreadsTokens _tokens;
    private readonly IHttpClientFactory _httpClientFactory;

    public ThreadsConnectionType(
        ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, ThreadsTokens tokens, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
    {
        _tokens = tokens;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override string ProviderName => ThreadsOAuthComposer.ProviderName;

    /// <inheritdoc />
    public override string? SetupDocsUrl => "https://developers.facebook.com/docs/threads/get-started";

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not ThreadsConnectionSettings { OAuthCredentialsId: { } id } || id == Guid.Empty)
        {
            return ConnectionValidationResult.Failure("No Threads account has been authorised for this connection.", null);
        }

        var token = await _tokens.GetAsync(id, cancellationToken);
        if (token is null)
        {
            return ConnectionValidationResult.Failure("The Threads token has expired (Threads tokens last 60 days unless they're used). Authorise the account again.", null);
        }

        // access_token as a query parameter, as the Threads reference shows (https://developers.facebook.com/docs/threads/reference/publishing).
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync("https://graph.threads.com/v1.0/me?fields=id,username&access_token=" + Uri.EscapeDataString(token), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ConnectionValidationResult.Failure($"Threads returned {(int)response.StatusCode} for the account.", [body]);
        }

        using var doc = JsonDocument.Parse(body);
        return ConnectionValidationResult.Success($"Threads is connected as @{doc.RootElement.GetProperty("username").GetString()}.");
    }
}
