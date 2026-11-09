namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Output of the <see cref="CreateStatusAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateStatusOutput
{
    public string StatusId { get; init; } = string.Empty;

    /// <summary>The status's public URL.</summary>
    public string PostUrl { get; init; } = string.Empty;
}
