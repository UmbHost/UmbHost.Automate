namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Output produced by the <see cref="SetIssueStateAction"/>.
/// </summary>
public sealed class SetIssueStateOutput
{
    public string? State { get; init; }
}
