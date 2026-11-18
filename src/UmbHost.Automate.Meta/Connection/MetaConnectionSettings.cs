using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Meta.Connection;

/// <summary>
/// Settings for a Meta connection: one Facebook Page, its Page access token, and optionally the
/// Instagram professional account linked to it.
/// </summary>
public sealed class MetaConnectionSettings
{
    [Field(Label = "Page ID", Description = "The Facebook Page's numeric id.", SortOrder = 0)]
    public string PageId { get; set; } = string.Empty;

    /// <summary>
    /// A Page access token. One obtained from a long-lived user token (via <c>/me/accounts</c>) has
    /// no expiry date, so the connection keeps working unless the token is revoked.
    /// </summary>
    [Field(Label = "Page Access Token", Description = "A Page access token derived from a long-lived user token (it then has no expiry date).", IsSensitive = true, SortOrder = 1)]
    public string PageAccessToken { get; set; } = "$Umbraco:Automate:Secrets:MetaPageAccessToken";

    [Field(Label = "Instagram Account ID", Description = "Optional. The Instagram professional account linked to the Page; needed for Instagram posts. Shown by Validate.", SortOrder = 2)]
    public string? InstagramAccountId { get; set; }

    /// <summary>
    /// The Graph API version, e.g. v27.0, or empty for the connector's current default (<see cref="MetaGraphVersion.Default"/>), which
    /// moves forward with each release. No initializer: the backoffice seeds a field with its default and saves it, which would pin
    /// every new connection to today's default.
    /// </summary>
    [Field(Label = "Graph API Version",
        Description = "e.g. v27.0. Leave empty to use the connector's current default (" + MetaGraphVersion.Default + "), which moves forward with each release. Meta supports each version for about two years.",
        EditorUiAlias = "Umb.PropertyEditorUi.TextBox",
        EditorConfig = "[{ \"alias\": \"placeholder\", \"value\": \"" + MetaGraphVersion.Default + "\" }]",
        SortOrder = 3)]
    [MetaGraphVersion]
    public string? GraphVersion { get; set; }
}
