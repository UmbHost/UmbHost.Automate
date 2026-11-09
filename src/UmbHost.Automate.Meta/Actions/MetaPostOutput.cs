namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Output of the Facebook and Instagram post actions, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class MetaPostOutput
{
    public string PostId { get; init; } = string.Empty;

    /// <summary>
    /// The post's public URL. Empty if Meta didn't return it; the post was still published.
    /// </summary>
    public string PostUrl { get; init; } = string.Empty;
}
