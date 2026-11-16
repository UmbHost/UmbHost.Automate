namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Output of the <see cref="CreateStatusAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateStatusOutput
{
    /// <summary>The status id on the posting instance, bound by UmbSocial as the post's external id.</summary>
    public string StatusId { get; init; } = string.Empty;

    /// <summary>The status's public URL.</summary>
    public string PostUrl { get; init; } = string.Empty;

    /// <summary>URLs of every post made, in order (one entry unless it is a thread).</summary>
    public IReadOnlyList<string> ThreadUrls { get; init; } = [];
}
