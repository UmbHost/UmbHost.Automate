using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teamwork.Connection;

/// <summary>
/// Settings for a Teamwork Projects connection.
/// </summary>
public sealed class TeamworkConnectionSettings
{
    /// <summary>
    /// Gets or sets the Teamwork subdomain, e.g. <c>umbhost</c> for <c>umbhost.teamwork.com</c>.
    /// </summary>
    [Field(Label = "Site", Description = "Your Teamwork subdomain, e.g. 'umbhost' for umbhost.teamwork.com.", SortOrder = 0)]
    public string Site { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a Teamwork Projects API token. Defaults to a config reference so the
    /// secret can live in user secrets / environment variables rather than the connection.
    /// </summary>
    [Field(Label = "API Token", Description = "A Teamwork Projects API token.", IsSensitive = true, SortOrder = 1)]
    public string ApiToken { get; set; } = "$Umbraco:Automate:Secrets:TeamworkApiToken";
}
