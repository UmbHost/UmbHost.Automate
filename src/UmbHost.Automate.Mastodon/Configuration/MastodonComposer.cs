using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;
using UmbHost.Automate.Mastodon.Http;

namespace UmbHost.Automate.Mastodon.Configuration;

/// <summary>
/// Registers the Mastodon connection type and actions with Umbraco Automate.
/// </summary>
public sealed class MastodonComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();
        MastodonClient.AddPublicClient(builder.Services);

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<MastodonConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateStatusAction>()
            .Add<GetPostMetricsAction>();
    }
}
