using Microsoft.Extensions.DependencyInjection;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Configuration;

/// <summary>
/// Registers the Facebook &amp; Instagram connection type and actions with Umbraco Automate.
/// </summary>
public sealed class MetaComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<MetaConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateFacebookPostAction>()
            .Add<CreateInstagramPostAction>();
    }
}
