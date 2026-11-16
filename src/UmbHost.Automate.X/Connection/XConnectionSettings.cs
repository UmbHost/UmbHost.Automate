using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.X.Connection;

/// <summary>
/// Settings for the X connection. The token is obtained via OAuth 2.0 (PKCE) and stored against the
/// credential id. OpenIddict still names the provider "Twitter".
/// </summary>
public sealed class XConnectionSettings
{
    [Field(Label = "X Account",
        Description = "Authenticate with X",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "Twitter" }]""")]
    public Guid? OAuthCredentialsId { get; set; }
}
