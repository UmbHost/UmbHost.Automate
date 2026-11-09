namespace UmbHost.Automate.Bluesky.Actions;

/// <summary>
/// Output of the <see cref="CreatePostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreatePostOutput
{
    /// <summary>The post's <c>at://</c> URI.</summary>
    public string Uri { get; init; } = string.Empty;

    public string Cid { get; init; } = string.Empty;

    /// <summary>The post's public bsky.app URL.</summary>
    public string PostUrl { get; init; } = string.Empty;
}
