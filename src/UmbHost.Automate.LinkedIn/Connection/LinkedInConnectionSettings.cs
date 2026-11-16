using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// Settings for the LinkedIn connection. The token is obtained via OAuth and stored against the
/// credential id rather than on the connection itself.
/// </summary>
public sealed class LinkedInConnectionSettings
{
    [Field(Label = "LinkedIn Account",
        Description = "Authorise as a member who is an administrator or content admin of the company page. LinkedIn tokens last 60 days; re-authorise when the connection reports it has expired.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "LinkedIn" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }

    /// <summary>
    /// The <c>LinkedIn-Version</c> header (YYYYMM). LinkedIn ships a version a month and retires each
    /// one after at least a year, so this must be moved forward about once a year.
    /// </summary>
    [Field(Label = "API Version", Description = "LinkedIn-Version header, YYYYMM. Each version is retired after about a year.", SortOrder = 1)]
    public string ApiVersion { get; set; } = "202610";
}
