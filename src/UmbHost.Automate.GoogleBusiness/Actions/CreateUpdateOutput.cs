namespace UmbHost.Automate.GoogleBusiness.Actions;

/// <summary>
/// Output of the <see cref="CreateUpdateAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateUpdateOutput
{
    /// <summary>The update's resource name, <c>accounts/…/locations/…/localPosts/…</c>.</summary>
    public string PostId { get; init; } = string.Empty;

    /// <summary>The update in Google Search (the post's <c>searchUrl</c>).</summary>
    public string PostUrl { get; init; } = string.Empty;
}
