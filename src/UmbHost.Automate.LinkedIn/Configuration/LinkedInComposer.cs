using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

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
            .Add<CreateOrganizationPostAction>();
    }
}

/// <summary>
/// Registers LinkedIn with OpenIddict Client WebIntegration so the connection can obtain tokens,
/// requesting the scopes company-page posting needs on top of any configured ones.
/// </summary>
/// <remarks>
/// Only registered when <c>Umbraco:Automate:Providers:LinkedIn:ClientId</c> is set: OpenIddict
/// rejects a registration without a client id at startup, which would stop the whole site booting.
/// </remarks>
public sealed class LinkedInOAuthComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config["Umbraco:Automate:Providers:LinkedIn:ClientId"]))
        {
            return;
        }

        builder.Services.AddOpenIddict()
            .AddClient(options => options.UseWebProviders()
                .AddLinkedIn(linkedIn => linkedIn.AddScopes("w_organization_social", "r_organization_social")));
    }
}
