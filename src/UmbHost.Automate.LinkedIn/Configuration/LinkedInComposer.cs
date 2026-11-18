using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Configuration;

/// <summary>
/// Registers the LinkedIn connection type and actions with Umbraco Automate.
/// </summary>
public sealed class LinkedInComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<LinkedInConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateOrganizationPostAction>()
            .Add<GetPostMetricsAction>();
    }
}

/// <summary>
/// Registers LinkedIn with the OpenIddict client by hand, as a plain OAuth 2.0 provider: authorisation and token endpoints only, no
/// userinfo, no <c>openid</c> and no <c>profile</c>. It asks for the scopes company-page posting needs on top of any configured ones,
/// plus <c>rw_organization_admin</c> for share statistics while <c>Umbraco:Automate:Providers:LinkedIn:Insights</c> is on. Automate's
/// own configurator then applies the client id, secret, extra scopes and its callback URL
/// (<c>umbraco/automate/oauth/callback/linkedin</c>), as it does for the built-in providers.
/// </summary>
/// <remarks>
/// <para>
/// Why not OpenIddict's built-in <c>AddLinkedIn()</c>: that provider is Sign In with LinkedIn (OpenID Connect). It always asks for the
/// <c>profile</c> scope (<c>Required="true"</c> in OpenIddictClientWebIntegrationProviders.xml), adds <c>openid</c> because LinkedIn's
/// discovery document lists it, and calls the userinfo endpoint. Those scopes come from the "Sign In with LinkedIn using OpenID
/// Connect" product, and LinkedIn won't put Community Management API on an app that has any other product, so a Community
/// Management app can't grant them and every authorisation failed. Core has no provider for a plain OAuth LinkedIn app, so this is
/// a small manual registration, the same shape as the Pinterest one. The provider name stays <c>LinkedIn</c>, so stored credentials
/// (which refresh by provider name) and the callback path are unchanged.
/// </para>
/// <para>
/// Endpoints and client authentication from LinkedIn's 3-legged flow,
/// https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow: <c>GET /oauth/v2/authorization</c>, then
/// <c>POST /oauth/v2/accessToken</c> with <c>client_id</c> and <c>client_secret</c> in the form body (client_secret_post).
/// </para>
/// <para>
/// Only registered when a client id is configured: OpenIddict rejects a registration without one at startup, which would stop the
/// whole site booting.
/// </para>
/// </remarks>
public sealed class LinkedInOAuthComposer : IComposer
{
    /// <summary>The OpenIddict provider name, also the configuration key under <c>Umbraco:Automate:Providers</c>.</summary>
    public const string ProviderName = "LinkedIn";

    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config[$"Umbraco:Automate:Providers:{ProviderName}:ClientId"]))
        {
            return;
        }

        string[] scopes = InsightsSwitch.IsOn(builder.Config, GetPostMetricsAction.InsightsSetting)
            ? ["w_organization_social", "r_organization_social", "rw_organization_admin"]
            : ["w_organization_social", "r_organization_social"];

        // The issuer LinkedIn's own discovery document gives (https://www.linkedin.com/oauth/.well-known/openid-configuration), which
        // the built-in provider used too. The configuration below is static, so that document is never fetched.
        var issuer = new Uri("https://www.linkedin.com/oauth");
        var registration = new OpenIddictClientRegistration
        {
            ProviderName = ProviderName,
            ProviderDisplayName = "LinkedIn",
            Issuer = issuer,
            Configuration = new OpenIddictConfiguration
            {
                Issuer = issuer,
                AuthorizationEndpoint = new Uri("https://www.linkedin.com/oauth/v2/authorization"),
                TokenEndpoint = new Uri("https://www.linkedin.com/oauth/v2/accessToken"),
                // No UserInfoEndpoint, and no ScopesSupported: OpenIddict only adds "openid" when the configuration lists it, and only
                // calls userinfo when an endpoint is set. Without either it asks for no identity and needs no subject claim.
                // Refresh tokens: LinkedIn issues them to apps enabled for programmatic refresh
                // (https://learn.microsoft.com/en-us/linkedin/shared/authentication/programmatic-refresh-tokens); Automate uses one if
                // the token response has it.
                GrantTypesSupported = { OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken },
                ResponseTypesSupported = { OpenIddictConstants.ResponseTypes.Code },
                ResponseModesSupported = { OpenIddictConstants.ResponseModes.Query },
                TokenEndpointAuthMethodsSupported = { OpenIddictConstants.ClientAuthenticationMethods.ClientSecretPost },
                // No CodeChallengeMethodsSupported, so no PKCE, as for Pinterest. LinkedIn's standard 3-legged flow documents no
                // code_challenge or code_verifier: its authorisation request takes response_type, client_id, redirect_uri, state and
                // scope, and its token request grant_type, code, client_id, client_secret and redirect_uri
                // (https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow). PKCE is a separate
                // flow for native apps on loopback redirect URLs that LinkedIn enables per app on request
                // (https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow-native). The built-in
                // provider sent none either: LinkedIn's discovery document lists no code_challenge_methods_supported. The client
                // authenticates with its secret and the state is protected.
            },
        };

        foreach (var scope in scopes)
        {
            registration.Scopes.Add(scope);
        }

        builder.Services.AddOpenIddict()
            .AddClient(options => options.AddRegistration(registration));
    }
}
