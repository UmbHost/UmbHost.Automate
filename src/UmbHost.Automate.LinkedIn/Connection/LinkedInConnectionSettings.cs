using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// Settings for the LinkedIn connection. The token is obtained via OAuth and stored against the
/// credential id rather than on the connection itself.
/// </summary>
public sealed class LinkedInConnectionSettings
{
    [Field(Label = "LinkedIn Account",
        Description = "Authorise as a member who is an Administrator of the company page (content admins can't connect). LinkedIn tokens last 60 days; re-authorise when the connection reports it has expired.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "LinkedIn" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }

    /// <summary>
    /// The <c>LinkedIn-Version</c> header (YYYYMM), or empty for the connector's current default
    /// (<see cref="LinkedInApiVersion.Default"/>), which moves forward with each release. LinkedIn ships a version a month and retires
    /// each one after at least a year, so a version named here must be moved forward about once a year. No initializer: the
    /// backoffice seeds a field with its default and saves it, which would pin every new connection to today's default.
    /// </summary>
    [Field(Label = "API Version",
        Description = "LinkedIn-Version header, YYYYMM. Leave empty to use the connector's current default (" + LinkedInApiVersion.Default + "), which moves forward with each release. LinkedIn retires each version about a year after release.",
        EditorUiAlias = "Umb.PropertyEditorUi.TextBox",
        EditorConfig = "[{ \"alias\": \"placeholder\", \"value\": \"" + LinkedInApiVersion.Default + "\" }]",
        SortOrder = 1)]
    [LinkedInApiVersion]
    public string? ApiVersion { get; set; }
}
