using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Settings for the <see cref="GetFacebookPostMetricsAction"/>.
/// </summary>
public sealed class GetFacebookPostMetricsSettings
{
    /// <summary>A JSON array of 1 to 100 Facebook post ids, bound by UmbSocial as <c>${ trigger.ids | json }</c>.</summary>
    [Field(Label = "Ids", Description = "A JSON array of 1 to 100 Facebook post ids, e.g. [\"123_456\"].", SortOrder = 0, SupportsBindings = true)]
    public string Ids { get; set; } = string.Empty;
}
