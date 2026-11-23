using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.GoogleBusiness.Actions;
using UmbHost.Automate.GoogleBusiness.Connection;

namespace UmbHost.Automate.GoogleBusiness.Configuration;

/// <summary>
/// Registers the Google Business Profile connection type and the Create Update action with Umbraco Automate.
/// </summary>
public sealed class GoogleBusinessComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<GoogleBusinessConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateUpdateAction>();
    }
}

/// <summary>
/// Registers OpenIddict's Google provider under the name <c>GoogleBusiness</c>, with the Business Profile scope and offline access
/// (a refresh token), so it has its own credentials (<c>Umbraco:Automate:Providers:GoogleBusiness</c>) and sits beside any other
/// Google connection.
/// </summary>
/// <remarks>
/// Only registered when a client id is configured: OpenIddict rejects a registration without one at startup, which would stop the
/// whole site booting. <c>prompt=consent</c> makes Google return a refresh token on every authorisation, not only the first.
/// </remarks>
public sealed class GoogleBusinessOAuthComposer : IComposer
{
    /// <summary>The OpenIddict provider name, also the configuration key under <c>Umbraco:Automate:Providers</c>.</summary>
    public const string ProviderName = "GoogleBusiness";

    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config[$"Umbraco:Automate:Providers:{ProviderName}:ClientId"]))
        {
            return;
        }

        // Automate renews tokens with the refresh grant but enables only the authorisation code flow, so it is enabled here.
        builder.Services.AddOpenIddict()
            .AddClient(options => options.AllowRefreshTokenFlow().UseWebProviders()
                .AddGoogle(google => google
                    .AddScopes("https://www.googleapis.com/auth/business.manage")
                    .SetAccessType("offline")
                    .SetPrompt("consent")
                    .SetProviderName(ProviderName)
                    .SetProviderDisplayName("Google Business Profile")));
    }
}
