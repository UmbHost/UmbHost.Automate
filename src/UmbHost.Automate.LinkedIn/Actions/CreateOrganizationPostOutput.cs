namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Output of the <see cref="CreateOrganizationPostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateOrganizationPostOutput
{
    /// <summary>The post URN, e.g. <c>urn:li:share:123</c>.</summary>
    public string PostUrn { get; init; } = string.Empty;

    public string PostUrl { get; init; } = string.Empty;
}
