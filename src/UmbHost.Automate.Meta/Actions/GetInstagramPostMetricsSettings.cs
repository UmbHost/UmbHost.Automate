using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Settings for the <see cref="GetInstagramPostMetricsAction"/>.
/// </summary>
public sealed class GetInstagramPostMetricsSettings
{
    /// <summary>A JSON array of 1 to 100 Instagram post ids, bound by UmbSocial as <c>${ trigger.ids | json }</c>.</summary>
    [Field(Label = "Ids", Description = "A JSON array of 1 to 100 Instagram post ids, e.g. [\"id1\",\"id2\"].", SortOrder = 0, SupportsBindings = true)]
    public string Ids { get; set; } = string.Empty;
}
