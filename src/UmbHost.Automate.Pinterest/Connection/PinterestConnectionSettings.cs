using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Pinterest.Connection;

/// <summary>
/// Settings for the Pinterest connection: which Pinterest environment to call, and how to sign in to it. With OAuth the token is
/// stored against the credential id rather than on the connection itself; with an access token it is the token generated on the
/// app page.
/// </summary>
/// <remarks>
/// The defaults (Production, OAuth) are the connection's behaviour before these settings existed, so a stored connection without
/// them keeps working unchanged.
/// </remarks>
public sealed class PinterestConnectionSettings
{
    [Field(Label = "Environment",
        Description = "Production posts real Pins. Sandbox (api-sandbox.pinterest.com) works on Trial access, keeps its boards and Pins apart from production, and can't make video Pins.",
        EditorUiAlias = "Umb.PropertyEditorUi.Dropdown",
        EditorConfig = """[{ "alias": "items", "value": ["Production", "Sandbox"] }]""",
        SortOrder = 0)]
    public PinterestEnvironment Environment { get; set; } = PinterestEnvironment.Production;

    [Field(Label = "Sign-in",
        Description = "OAuth: authorise a Pinterest account; tokens renew automatically (production only). Access token: a token generated on the app's Configure tab, for either environment.",
        EditorUiAlias = "Umb.PropertyEditorUi.Dropdown",
        EditorConfig = """[{ "alias": "items", "value": ["OAuth", "AccessToken"] }]""",
        SortOrder = 1)]
    public PinterestSignIn SignIn { get; set; } = PinterestSignIn.OAuth;

    [Field(Label = "Pinterest Account",
        Description = "Authorise as the Pinterest business account that owns the boards. Tokens renew automatically. Production only.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "Pinterest" }]""",
        VisibleWhen = nameof(SignIn),
        VisibleWhenValues = [nameof(PinterestSignIn.OAuth)],
        SortOrder = 2)]
    public Guid? OAuthCredentialsId { get; set; }

    /// <summary>
    /// A token from the app's Configure tab (Generate Access Token), for the environment chosen above. Empty by default: a default
    /// secret reference would be resolved for every Pinterest connection, OAuth ones too, and fail them when the secret isn't set.
    /// </summary>
    [Field(Label = "Access Token",
        Description = "From developers.pinterest.com/apps → your app → Configure → Generate Access Token, for the environment above: sandbox and production tokens aren't interchangeable. It lasts 30 days and can't be refreshed, so generate a new one when it expires. Best kept as a secret, e.g. $Umbraco:Automate:Secrets:PinterestAccessToken.",
        IsSensitive = true,
        VisibleWhen = nameof(SignIn),
        VisibleWhenValues = [nameof(PinterestSignIn.AccessToken)],
        SortOrder = 3)]
    public string? AccessToken { get; set; }
}

/// <summary>Which Pinterest API host a connection calls.</summary>
public enum PinterestEnvironment
{
    /// <summary><c>https://api.pinterest.com/v5</c>.</summary>
    Production,

    /// <summary><c>https://api-sandbox.pinterest.com/v5</c>: https://developers.pinterest.com/docs/developer-tools/sandbox/.</summary>
    Sandbox,
}

/// <summary>How a Pinterest connection gets its bearer token.</summary>
public enum PinterestSignIn
{
    /// <summary>Automate's OAuth credential (the Authenticate button).</summary>
    OAuth,

    /// <summary>A static token generated on the app page.</summary>
    AccessToken,
}
