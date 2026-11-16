using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Threads.Connection;

/// <summary>
/// Settings for the Threads connection. The token is obtained via OAuth and stored against the credential id rather than on the
/// connection itself.
/// </summary>
public sealed class ThreadsConnectionSettings
{
    [Field(Label = "Threads Account",
        Description = "Authorise as the Threads profile to post as. The token lasts 60 days and is renewed while the connection is in use; after 60 days unused, authorise again.",
        EditorUiAlias = "Umb.Automate.OAuth",
        EditorConfig = """[{ "alias": "provider", "value": "Threads" }]""",
        SortOrder = 0)]
    public Guid? OAuthCredentialsId { get; set; }
}
