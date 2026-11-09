using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Mastodon.Connection;

/// <summary>
/// Settings for a Mastodon connection: an instance and an application access token.
/// </summary>
public sealed class MastodonConnectionSettings
{
    [Field(Label = "Instance URL", Description = "The account's server, e.g. https://mastodon.social.", SortOrder = 0)]
    public string InstanceUrl { get; set; } = string.Empty;

    /// <summary>
    /// An access token from Preferences → Development → New application, with the
    /// <c>write:statuses</c> and <c>write:media</c> scopes.
    /// </summary>
    [Field(Label = "Access Token", Description = "From Preferences → Development → New application (scopes: write:statuses, write:media).", IsSensitive = true, SortOrder = 1)]
    public string AccessToken { get; set; } = "$Umbraco:Automate:Secrets:MastodonAccessToken";
}
