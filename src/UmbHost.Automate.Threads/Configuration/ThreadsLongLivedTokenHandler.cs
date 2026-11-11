using OpenIddict.Abstractions;
using OpenIddict.Client;
using UmbHost.Automate.Threads.Connection;
using static OpenIddict.Client.OpenIddictClientEvents;
using static OpenIddict.Client.OpenIddictClientHandlers;

namespace UmbHost.Automate.Threads.Configuration;

/// <summary>
/// Swaps the one-hour token Threads issues at sign-in for a 60-day one, inside the OAuth callback and before Automate stores it, so a
/// connection never holds a token that lapses within the hour.
/// </summary>
/// <remarks>
/// An OpenIddict client event handler (OpenIddict's own extension point for provider quirks): Automate stores what the callback's
/// authentication result carries, and has no hook of its own between receiving a token and saving it. Threads' exchange isn't standard
/// OAuth: <c>GET https://graph.threads.com/access_token?grant_type=th_exchange_token&amp;client_secret=…&amp;access_token=…</c>
/// (https://developers.facebook.com/docs/threads/get-started/long-lived-tokens). If it fails, the authorisation fails, rather than storing
/// a token that dies within the hour.
/// </remarks>
public sealed class ThreadsLongLivedTokenHandler : IOpenIddictClientHandler<ProcessAuthenticationContext>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ThreadsLongLivedTokenHandler(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    /// <summary>Runs once the token response's access token has been read, before the result reaches Automate's callback.</summary>
    public static OpenIddictClientHandlerDescriptor Descriptor { get; } = OpenIddictClientHandlerDescriptor.CreateBuilder<ProcessAuthenticationContext>()
        .UseSingletonHandler<ThreadsLongLivedTokenHandler>()
        .SetOrder(ValidateBackchannelAccessToken.Descriptor.Order + 500)
        .SetType(OpenIddictClientHandlerType.Custom)
        .Build();

    public async ValueTask HandleAsync(ProcessAuthenticationContext context)
    {
        if (context.Registration?.ProviderName != ThreadsOAuthComposer.ProviderName || string.IsNullOrEmpty(context.BackchannelAccessToken))
        {
            return;
        }

        using var client = _httpClientFactory.CreateClient();
        var url = "https://graph.threads.com/access_token?grant_type=th_exchange_token"
            + "&client_secret=" + Uri.EscapeDataString(context.Registration.ClientSecret ?? string.Empty)
            + "&access_token=" + Uri.EscapeDataString(context.BackchannelAccessToken);
        try
        {
            using var response = await client.GetAsync(url, context.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                context.Reject(OpenIddictConstants.Errors.ServerError, $"Threads refused to issue a long-lived token ({(int)response.StatusCode}).");
                return;
            }

            if (!ThreadsTokenAnswer.TryRead(body, out var accessToken, out var expiresIn))
            {
                context.Reject(OpenIddictConstants.Errors.ServerError, "Threads didn't issue a long-lived token: its answer had no usable access_token and expires_in.");
                return;
            }

            context.BackchannelAccessToken = accessToken;
            context.BackchannelAccessTokenExpirationDate = DateTimeOffset.UtcNow.Add(expiresIn);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            context.Reject(OpenIddictConstants.Errors.ServerError, "Threads didn't issue a long-lived token: " + ex.Message);
        }
    }
}
