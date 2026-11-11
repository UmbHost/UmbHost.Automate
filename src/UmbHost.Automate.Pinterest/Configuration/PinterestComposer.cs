using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using Umbraco.Cms.Core.Composing;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Pinterest.Actions;
using UmbHost.Automate.Pinterest.Connection;

namespace UmbHost.Automate.Pinterest.Configuration;

/// <summary>
/// Registers the Pinterest connection type and the Create Pin action with Umbraco Automate.
/// </summary>
public sealed class PinterestComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<PinterestConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreatePinAction>();
    }
}

/// <summary>
/// Registers Pinterest with the OpenIddict client. OpenIddict has no Pinterest web provider, so this is a manual registration with
/// Pinterest's endpoints; Automate's own configurator then applies the client id, secret, extra scopes and its callback URL, as it
/// does for the built-in providers (<c>Umbraco:Automate:Providers:Pinterest</c>).
/// </summary>
/// <remarks>
/// Endpoints and client authentication from
/// https://developers.pinterest.com/docs/getting-started/set-up-authentication-and-authorization/ (HTTP Basic on the token
/// endpoint; 30-day access tokens with a refresh token). Only registered when a client id is configured: OpenIddict rejects a
/// registration without one at startup, which would stop the whole site booting.
/// </remarks>
public sealed class PinterestOAuthComposer : IComposer
{
    /// <summary>The OpenIddict provider name, also the configuration key under <c>Umbraco:Automate:Providers</c>.</summary>
    public const string ProviderName = "Pinterest";

    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config[$"Umbraco:Automate:Providers:{ProviderName}:ClientId"]))
        {
            return;
        }

        var issuer = new Uri("https://www.pinterest.com/");
        builder.Services.AddOpenIddict()
            .AddClient(options => options.AddRegistration(new OpenIddictClientRegistration
            {
                ProviderName = ProviderName,
                ProviderDisplayName = "Pinterest",
                Issuer = issuer,
                Scopes = { "boards:read", "pins:write" },
                Configuration = new OpenIddictConfiguration
                {
                    Issuer = issuer,
                    AuthorizationEndpoint = new Uri("https://www.pinterest.com/oauth/"),
                    TokenEndpoint = new Uri("https://api.pinterest.com/v5/oauth/token"),
                    GrantTypesSupported = { OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken },
                    ResponseTypesSupported = { OpenIddictConstants.ResponseTypes.Code },
                    ResponseModesSupported = { OpenIddictConstants.ResponseModes.Query },
                    TokenEndpointAuthMethodsSupported = { OpenIddictConstants.ClientAuthenticationMethods.ClientSecretBasic },
                },
            }));
    }
}
