using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// Settings for the LinkedIn profile connection: a member posting as themselves, through a second LinkedIn app with Share on LinkedIn and
/// Sign In with LinkedIn using OpenID Connect. The token is obtained via OAuth and stored against the credential id.
/// </summary>
public sealed class LinkedInProfileConnectionSettings
{
    [Field(Label = "LinkedIn Account",
        Description = "Authorise as the member whose profile posts go to. LinkedIn tokens last 60 days; re-authorise when the connection reports it has expired.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "LinkedInProfile" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }

    /// <summary>
    /// The <c>LinkedIn-Version</c> header (YYYYMM). LinkedIn ships a version a month and retires each
    /// one after at least a year, so this must be moved forward about once a year.
    /// </summary>
    [Field(Label = "API Version", Description = "LinkedIn-Version header, YYYYMM. Each version is retired after about a year.", SortOrder = 1)]
    public string ApiVersion { get; set; } = "202610";
}
