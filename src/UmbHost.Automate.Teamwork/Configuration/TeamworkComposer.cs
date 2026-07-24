using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Teamwork.Actions;
using UmbHost.Automate.Teamwork.Connection;

namespace UmbHost.Automate.Teamwork.Configuration;

/// <summary>
/// Registers the Teamwork connection type and actions with Umbraco Automate.
/// </summary>
public sealed class TeamworkComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<TeamworkConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateTaskAction>();
    }
}
