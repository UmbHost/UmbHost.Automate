namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Output of the <see cref="CreateMemberPostAction"/>, bindable as <c>${ steps.&lt;id&gt;.postUrl }</c>.
/// </summary>
public sealed class CreateMemberPostOutput
{
    /// <summary>The post URN, e.g. <c>urn:li:share:123</c>, bound by UmbSocial as the post's external id.</summary>
    public string PostUrn { get; init; } = string.Empty;

    public string PostUrl { get; init; } = string.Empty;

    /// <summary>Set when the post is live but the first comment could not be added.</summary>
    public string? Warning { get; init; }
}
