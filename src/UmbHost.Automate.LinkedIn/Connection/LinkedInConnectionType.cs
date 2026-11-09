using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// LinkedIn connection type using OAuth via OpenIddict WebIntegration. Validation checks that a
/// current access token is available; LinkedIn issues no refresh token to most apps, so an expired
/// connection must be re-authorised.
/// </summary>
[ConnectionType("linkedin", "LinkedIn",
    Group = "Social",
    Icon = "icon-linkedin",
    Description = "Connect to LinkedIn to post as a company page")]
public sealed class LinkedInConnectionType : OAuthConnectionTypeBase<LinkedInConnectionSettings>
{
    public LinkedInConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService)
        : base(infrastructure, credentialsService)
    {
    }

    /// <inheritdoc />
    public override string ProviderName => "LinkedIn";
}
