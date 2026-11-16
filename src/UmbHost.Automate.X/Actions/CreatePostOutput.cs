namespace UmbHost.Automate.X.Actions;

/// <summary>
/// Output of the <see cref="CreatePostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreatePostOutput
{
    /// <summary>The post id (the first post's, for a thread), bound by UmbSocial as the post's external id.</summary>
    public string PostId { get; init; } = string.Empty;

    public string PostUrl { get; init; } = string.Empty;

    /// <summary>URLs of every post made, in order (one entry unless it is a thread).</summary>
    public IReadOnlyList<string> ThreadUrls { get; init; } = [];
}
