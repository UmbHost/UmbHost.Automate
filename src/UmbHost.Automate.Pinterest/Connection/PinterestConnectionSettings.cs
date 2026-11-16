using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Pinterest.Connection;

/// <summary>
/// Settings for the Pinterest connection. The token is obtained via OAuth and stored against the credential id rather than on the
/// connection itself.
/// </summary>
public sealed class PinterestConnectionSettings
{
    [Field(Label = "Pinterest Account",
        Description = "Authorise as the Pinterest business account that owns the boards. Tokens renew automatically.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "Pinterest" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }
}
