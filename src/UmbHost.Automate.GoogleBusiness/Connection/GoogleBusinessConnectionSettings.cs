using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.GoogleBusiness.Connection;

/// <summary>
/// Settings for the Google Business Profile connection. The token is obtained via OAuth and stored against the credential id rather
/// than on the connection itself.
/// </summary>
public sealed class GoogleBusinessConnectionSettings
{
    [Field(Label = "Google Account",
        Description = "Authorise as a Google account that is an owner or manager of the business profile. Tokens renew automatically.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "GoogleBusiness" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }
}
