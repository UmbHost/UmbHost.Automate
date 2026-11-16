using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Pinterest.Actions;

/// <summary>
/// Settings for the <see cref="GetPostMetricsAction"/>.
/// </summary>
public sealed class GetPostMetricsSettings
{
    /// <summary>A JSON array of 1 to 100 post ids, bound by UmbSocial as <c>${ trigger.ids | json }</c>.</summary>
    [Field(Label = "Ids", Description = "A JSON array of 1 to 100 post ids, e.g. [\"id1\",\"id2\"].", SortOrder = 0, SupportsBindings = true)]
    public string Ids { get; set; } = string.Empty;
}
