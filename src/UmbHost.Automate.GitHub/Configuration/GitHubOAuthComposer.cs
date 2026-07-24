using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace UmbHost.Automate.GitHub.Configuration;

/// <summary>
/// Registers the GitHub OAuth provider with OpenIddict Client WebIntegration so the GitHub
/// connection type can obtain and refresh access tokens.
/// </summary>
public sealed class GitHubOAuthComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOpenIddict()
            .AddClient(options => options.UseWebProviders().AddGitHub(_ => { }));
    }
}
