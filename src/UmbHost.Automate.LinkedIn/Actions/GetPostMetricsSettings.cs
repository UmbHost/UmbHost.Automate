using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Settings for the <see cref="GetPostMetricsAction"/>.
/// </summary>
public sealed class GetPostMetricsSettings
{
    /// <summary>The page's numeric id, as the channel's post step has it (share statistics are read per organisation).</summary>
    [Field(Label = "Organization ID", Description = "The company page's numeric id, as on the post step.", SortOrder = 0, SupportsBindings = true)]
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>A JSON array of 1 to 100 post URNs (<c>urn:li:share:…</c> or <c>urn:li:ugcPost:…</c>), bound by UmbSocial as <c>${ trigger.ids | json }</c>.</summary>
    [Field(Label = "Ids", Description = "A JSON array of 1 to 100 post URNs, e.g. [\"urn:li:share:1\",\"urn:li:ugcPost:2\"].", SortOrder = 1, SupportsBindings = true)]
    public string Ids { get; set; } = string.Empty;
}
