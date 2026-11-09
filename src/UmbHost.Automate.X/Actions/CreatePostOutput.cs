namespace UmbHost.Automate.X.Actions;

/// <summary>
/// Output of the <see cref="CreatePostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreatePostOutput
{
    public string PostId { get; init; } = string.Empty;

    public string PostUrl { get; init; } = string.Empty;
}
