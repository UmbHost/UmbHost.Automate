namespace UmbHost.Automate.Threads.Actions;

/// <summary>
/// Output of the <see cref="CreateThreadsPostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateThreadsPostOutput
{
    /// <summary>The first published media id, bound by UmbSocial as the post's external id.</summary>
    public string PostId { get; init; } = string.Empty;

    public string PostUrl { get; init; } = string.Empty;

    /// <summary>URLs of every post made, in order (one entry unless it is a thread).</summary>
    public IReadOnlyList<string> ThreadUrls { get; init; } = [];
}
