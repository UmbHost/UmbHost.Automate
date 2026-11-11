namespace UmbHost.Automate.Pinterest.Actions;

/// <summary>
/// Output of the <see cref="CreatePinAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreatePinOutput
{
    /// <summary>The Pin's id.</summary>
    public string PostId { get; init; } = string.Empty;

    /// <summary>The Pin's page, <c>https://www.pinterest.com/pin/{id}/</c>.</summary>
    public string PostUrl { get; init; } = string.Empty;
}
