namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Output produced by the <see cref="AddIssueCommentAction"/>.
/// </summary>
public sealed class AddIssueCommentOutput
{
    public long CommentId { get; init; }

    public string? HtmlUrl { get; init; }
}
