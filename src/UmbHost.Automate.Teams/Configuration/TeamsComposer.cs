using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Teams.Actions;
using UmbHost.Automate.Teams.Connection;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Configuration;

/// <summary>
/// Registers the Teams connection type and action with Umbraco Automate.
/// </summary>
public sealed class TeamsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();
        TeamsWebhookClient.Register(builder.Services);

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<TeamsConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<PostCardAction>();
    }
}
