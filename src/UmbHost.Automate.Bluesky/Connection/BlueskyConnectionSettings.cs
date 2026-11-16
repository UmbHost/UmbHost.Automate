using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Bluesky.Connection;

/// <summary>
/// Settings for a Bluesky connection: an account and an app password.
/// </summary>
public sealed class BlueskyConnectionSettings
{
    [Field(Label = "Service URL", Description = "The account's PDS. Leave as https://bsky.social unless the account is self-hosted.", SortOrder = 0)]
    public string ServiceUrl { get; set; } = "https://bsky.social";

    [Field(Label = "Handle", Description = "The account handle, e.g. umbhost.net.", SortOrder = 1)]
    public string Identifier { get; set; } = string.Empty;

    /// <summary>
    /// An app password (Settings → Privacy and security → App passwords), never the account password.
    /// Defaults to a config reference so it can live in environment variables.
    /// </summary>
    [Field(Label = "App Password", Description = "A Bluesky app password, not the account password.", IsSensitive = true, SortOrder = 2)]
    public string AppPassword { get; set; } = "$Umbraco:Automate:Secrets:BlueskyAppPassword";
}
