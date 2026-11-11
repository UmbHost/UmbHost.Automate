using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Threads.Actions;
using UmbHost.Automate.Threads.Connection;

namespace UmbHost.Automate.Threads.Configuration;

/// <summary>
/// Registers the Threads connection type and the Create Threads Post action with Umbraco Automate.
/// </summary>
public sealed class ThreadsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<ThreadsTokens>();

        builder.WithCollectionBuilder<ConnectionTypeCollectionBuilder>()
            .Add<ThreadsConnectionType>();

        builder.WithCollectionBuilder<ActionCollectionBuilder>()
            .Add<CreateThreadsPostAction>();
    }
}

/// <summary>
/// Registers Threads with the OpenIddict client. OpenIddict has no Threads web provider, so this is a manual registration with
/// Threads' endpoints; Automate's own configurator then applies the client id, secret, extra scopes and its callback URL, as it does for
/// the built-in providers (<c>Umbraco:Automate:Providers:Threads</c>). The Threads app id and secret are the ones in the Meta app's
/// "Threads" use case.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints from https://developers.facebook.com/docs/threads/get-started/get-access-tokens-and-permissions (the app secret is sent in
/// the token request's body). Threads issues no refresh token, so <see cref="ThreadsLongLivedTokenHandler"/> swaps the one-hour token for
/// a 60-day one during the callback, before Automate stores it, and <see cref="ThreadsTokens"/> refreshes it from then on.
/// </para>
/// <para>
/// Only registered when a client id is configured: OpenIddict rejects a registration without one at startup, which would stop the whole
/// site booting.
/// </para>
/// </remarks>
public sealed class ThreadsOAuthComposer : IComposer
{
    /// <summary>The OpenIddict provider name, also the configuration key under <c>Umbraco:Automate:Providers</c>.</summary>
    public const string ProviderName = "Threads";

    public void Compose(IUmbracoBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Config[$"Umbraco:Automate:Providers:{ProviderName}:ClientId"]))
        {
            return;
        }

        builder.Services.AddHttpClient();
        var issuer = new Uri("https://threads.com/");
        builder.Services.AddOpenIddict()
            .AddClient(options =>
            {
                options.AddRegistration(new OpenIddictClientRegistration
                {
                    ProviderName = ProviderName,
                    ProviderDisplayName = "Threads",
                    Issuer = issuer,
                    Scopes = { "threads_basic", "threads_content_publish", "threads_manage_replies" },
                    Configuration = new OpenIddictConfiguration
                    {
                        Issuer = issuer,
                        AuthorizationEndpoint = new Uri("https://threads.com/oauth/authorize"),
                        TokenEndpoint = new Uri("https://graph.threads.com/oauth/access_token"),
                        GrantTypesSupported = { OpenIddictConstants.GrantTypes.AuthorizationCode },
                        ResponseTypesSupported = { OpenIddictConstants.ResponseTypes.Code },
                        ResponseModesSupported = { OpenIddictConstants.ResponseModes.Query },
                        TokenEndpointAuthMethodsSupported = { OpenIddictConstants.ClientAuthenticationMethods.ClientSecretPost },
                    },
                });
                options.AddEventHandler(ThreadsLongLivedTokenHandler.Descriptor);
            });
    }
}
