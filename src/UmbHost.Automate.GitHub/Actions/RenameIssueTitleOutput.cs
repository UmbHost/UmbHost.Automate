namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Output produced by the <see cref="RenameIssueTitleAction"/>.
/// </summary>
public sealed class RenameIssueTitleOutput
{
    public long IssueNumber { get; init; }

    public string? HtmlUrl { get; init; }
}
