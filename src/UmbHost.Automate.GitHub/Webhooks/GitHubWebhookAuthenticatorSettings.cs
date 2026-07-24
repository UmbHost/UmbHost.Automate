using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.GitHub.Webhooks;

/// <summary>
/// Settings for the <see cref="GitHubWebhookAuthenticator"/>.
/// </summary>
public sealed class GitHubWebhookAuthenticatorSettings
{
    /// <summary>
    /// Gets or sets the secret configured on the GitHub webhook (used as the HMAC key).
    /// </summary>
    [Field(Label = "Webhook Secret", Description = "The secret configured on the GitHub webhook.", IsSensitive = true)]
    public string SigningKey { get; set; } = string.Empty;
}
