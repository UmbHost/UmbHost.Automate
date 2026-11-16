using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.Threads.Connection;

/// <summary>
/// The Threads token for a connection, refreshed when it is a week old. Threads' long-lived tokens last 60 days from issue or refresh
/// and are refreshed by their own call, not OAuth's refresh grant (Automate's refresh needs a refresh token, which Threads doesn't
/// issue), so this does it and saves the new token through Automate's credentials service.
/// </summary>
/// <remarks>
/// <c>GET https://graph.threads.com/refresh_access_token?grant_type=th_refresh_token&amp;access_token=…</c>; the token must be at least
/// 24 hours old and not yet expired (https://developers.facebook.com/docs/threads/get-started/long-lived-tokens). The token's age is
/// the credentials' <c>DateModified</c>, which Automate sets when it stores or updates them.
/// </remarks>
public sealed class ThreadsTokens
{
    /// <summary>How long a long-lived token lasts from issue or refresh.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    /// <summary>The age at which a token is refreshed (well inside its 60 days, and past Threads' 24-hour minimum).</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public ThreadsTokens(IHttpClientFactory httpClientFactory, IOAuthCredentialsService credentialsService)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    /// <summary>A current token, refreshed first when it is due; null when there is none or it has lapsed (re-authorise).</summary>
    public async Task<string?> GetAsync(Guid credentialsId, CancellationToken cancellationToken)
    {
        var credentials = await _credentialsService.GetCredentialsAsync(credentialsId, cancellationToken);
        if (credentials is null || string.IsNullOrEmpty(credentials.AccessToken))
        {
            return null;
        }

        var age = DateTime.UtcNow - credentials.DateModified;
        if (age >= Lifetime)
        {
            return null;
        }

        if (age < RefreshAfter)
        {
            return credentials.AccessToken;
        }

        using var client = _httpClientFactory.CreateClient();
        try
        {
            using var response = await client.GetAsync(
                "https://graph.threads.com/refresh_access_token?grant_type=th_refresh_token&access_token=" + Uri.EscapeDataString(credentials.AccessToken),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The current token still works until it lapses; the next post tries the refresh again.
                return credentials.AccessToken;
            }

            if (!ThreadsTokenAnswer.TryRead(await response.Content.ReadAsStringAsync(cancellationToken), out var accessToken, out _))
            {
                // Not a token: keep the current one, which works until it lapses; the next post tries the refresh again.
                return credentials.AccessToken;
            }

            credentials.AccessToken = accessToken;
            credentials.ExpiresUtc = null;
            await _credentialsService.UpdateCredentialsAsync(credentials, cancellationToken);
            return credentials.AccessToken;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return credentials.AccessToken;
        }
    }
}
