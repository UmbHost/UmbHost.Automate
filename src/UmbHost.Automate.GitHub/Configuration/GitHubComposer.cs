using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Bindings;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Triggers.Webhooks;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.GitHub.Actions;
using UmbHost.Automate.GitHub.Bindings;
using UmbHost.Automate.GitHub.Connection;
using UmbHost.Automate.GitHub.Webhooks;

namespace UmbHost.Automate.GitHub.Configuration;

/// <summary>
/// Registers the GitHub jsonpath binding filter, webhook authenticator, connection type and actions
/// with Umbraco Automate.
/// </summary>
public sealed class GitHubComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.WithCollectionBuilder<BindingFilterCollectionBuilder>()
            .Add<JsonPathBindingFilter>();

        builder.WithCollectionBuilder<WebhookAuthenticatorCollectionBuilder>()
            .Add<GitHubWebhookAuthenticator>();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<GitHubConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<RenameIssueTitleAction>()
            .Add<AddIssueCommentAction>()
            .Add<SetIssueStateAction>();
    }
}
