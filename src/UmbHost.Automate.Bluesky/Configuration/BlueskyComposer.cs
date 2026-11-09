using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Configuration;

/// <summary>
/// Registers the Bluesky connection type and actions with Umbraco Automate.
/// </summary>
public sealed class BlueskyComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<BlueskyConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreatePostAction>();
    }
}
