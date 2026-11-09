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

    [Field(Label = "Graph API Version", Description = "e.g. v27.0. Each version is supported for about two years.", SortOrder = 3)]
    public string GraphVersion { get; set; } = "v27.0";
}
