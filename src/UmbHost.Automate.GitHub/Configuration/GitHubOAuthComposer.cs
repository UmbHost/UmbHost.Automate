using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace UmbHost.Automate.GitHub.Configuration;

/// <summary>
/// Registers the GitHub OAuth provider with OpenIddict Client WebIntegration so the GitHub
/// connection type can obtain and refresh access tokens.
/// </summary>
/// <remarks>
/// The provider is only registered when a client id is configured at
/// <c>Umbraco:Automate:Providers:GitHub:ClientId</c>. OpenIddict validates every client
/// registration at startup and throws if one has no client id, so registering unconditionally
/// would stop the whole site booting until GitHub OAuth is set up. Guarding the registration
/// lets the rest of the package (and other providers) work without GitHub credentials.
/// </remarks>
public sealed class GitHubOAuthComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        var clientId = builder.Config["Umbraco:Automate:Providers:GitHub:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        builder.Services.AddOpenIddict()
            .AddClient(options => options.UseWebProviders().AddGitHub(_ => { }));
    }
}
