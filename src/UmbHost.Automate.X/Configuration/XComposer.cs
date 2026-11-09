using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Configuration;

/// <summary>
/// Registers the X connection type and actions with Umbraco Automate.
/// </summary>
public sealed class XComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<XConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreatePostAction>();
    }
}

/// <summary>
/// Registers X with OpenIddict Client WebIntegration (where the provider is still named "Twitter"),
/// requesting the scopes posting needs. <c>offline.access</c> gets a refresh token, so the
/// two-hour access tokens renew without re-authorising.
/// </summary>
/// <remarks>
/// Only registered when <c>Umbraco:Automate:Providers:Twitter:ClientId</c> is set: OpenIddict rejects
/// a registration without a client id at startup, which would stop the whole site booting.
/// </remarks>
public sealed class XOAuthComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config["Umbraco:Automate:Providers:Twitter:ClientId"]))
        {
            return;
        }

        builder.Services.AddOpenIddict()
            .AddClient(options => options.UseWebProviders()
                .AddTwitter(x => x.AddScopes("tweet.read", "tweet.write", "users.read", "media.write", "offline.access")));
    }
}
