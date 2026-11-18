using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;

namespace UmbHost.Automate.Pinterest.Connection;

/// <summary>
/// What a Pinterest connection's settings mean for a request: the API host and the bearer token. Shared by the connection test and
/// the actions so they can't disagree about which environment or token is used.
/// </summary>
/// <remarks>
/// Sandbox is the production API on another host, with its own tokens and its own boards and Pins
/// (https://developers.pinterest.com/docs/developer-tools/sandbox/).
/// </remarks>
internal static class PinterestApi
{
    public const string ProductionBase = "https://api.pinterest.com/v5/";
    public const string SandboxBase = "https://api-sandbox.pinterest.com/v5/";

    private const string AppPage = "https://developers.pinterest.com/apps/";

    /// <summary>The v5 base URL, with a trailing slash, for the connection's environment.</summary>
    public static string BaseUrl(PinterestConnectionSettings settings)
        => settings.Environment == PinterestEnvironment.Sandbox ? SandboxBase : ProductionBase;

    /// <summary>"Production, OAuth", "Sandbox, access token" and so on, so a test result can't be mistaken for the other kind.</summary>
    public static string Describe(PinterestConnectionSettings settings)
        => $"{settings.Environment}, {(settings.SignIn == PinterestSignIn.AccessToken ? "access token" : "OAuth")}";

    /// <summary>
    /// The bearer token, or why there isn't one. A Sandbox connection signed in with OAuth has none: Automate binds a connection
    /// type to one OpenIddict provider, whose token endpoint is production's, and a production token isn't accepted by the sandbox.
    /// </summary>
    public static async Task<(string? Token, string? Error)> GetTokenAsync(
        PinterestConnectionSettings settings, IOAuthCredentialsService credentials, CancellationToken cancellationToken)
    {
        if (settings.SignIn == PinterestSignIn.AccessToken)
        {
            return string.IsNullOrWhiteSpace(settings.AccessToken)
                ? (null, $"No Pinterest access token is set. Generate one for {settings.Environment} on the app's Configure tab ({AppPage}).")
                : (settings.AccessToken.Trim(), null);
        }

        if (settings.Environment == PinterestEnvironment.Sandbox)
        {
            return (null, "OAuth sign-in only reaches production. For Sandbox, set Sign-in to Access token and use a sandbox token from the app's Configure tab.");
        }

        if (settings.OAuthCredentialsId is not { } id || id == Guid.Empty)
        {
            return (null, "A Pinterest connection is required.");
        }

        var token = await credentials.GetValidAccessTokenAsync(id, cancellationToken);
        return string.IsNullOrEmpty(token)
            ? (null, "The Pinterest token is expired or revoked. Re-authorise the connection.")
            : (token, null);
    }

    /// <summary>The step failure for a missing token.</summary>
    public static ActionResult NoToken(string error)
        => ActionResult.Failed(new UnauthorizedAccessException(error), StepRunErrorCategory.Authentication);

    /// <summary>
    /// The step failure for a 401 to an access token, which can't be refreshed: it has expired or belongs to the other environment.
    /// </summary>
    public static ActionResult AccessTokenRejected(PinterestConnectionSettings settings)
        => ActionResult.Failed(new UnauthorizedAccessException(AccessTokenRejectedMessage(settings)), StepRunErrorCategory.Authentication);

    public static string AccessTokenRejectedMessage(PinterestConnectionSettings settings)
        => $"Pinterest rejected the access token (401): it has expired (they last 30 days) or wasn't generated for {settings.Environment}. "
           + $"Generate a new {settings.Environment} token on the app's Configure tab ({AppPage}) and update the connection.";
}
