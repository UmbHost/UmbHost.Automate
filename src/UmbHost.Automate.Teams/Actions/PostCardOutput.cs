namespace UmbHost.Automate.Teams.Actions;

/// <summary>
/// Output of the <see cref="PostCardAction"/>. Teams gives no post id or address, so there is no
/// PostUrl, and nothing here may carry the webhook address.
/// </summary>
public sealed class PostCardOutput
{
    /// <summary>True when the Workflow accepted the card (it still cannot confirm the card appeared).</summary>
    public bool Accepted { get; init; }

    /// <summary>What an editor should know about the result.</summary>
    public string Note { get; init; } = string.Empty;
}
