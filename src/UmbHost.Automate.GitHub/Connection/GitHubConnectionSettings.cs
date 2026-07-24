using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.GitHub.Connection;

/// <summary>
/// Settings for the GitHub connection type. The token is obtained via OAuth and stored
/// against the credential id rather than on the connection itself.
/// </summary>
public sealed class GitHubConnectionSettings
{
    [Field(Label = "GitHub Account",
        Description = "Authenticate with GitHub",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "GitHub" }]""")]
    public Guid? OAuthCredentialsId { get; set; }
}
