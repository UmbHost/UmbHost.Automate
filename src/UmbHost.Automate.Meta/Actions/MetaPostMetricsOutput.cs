using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Output of the Facebook and Instagram metrics actions, bindable as <c>${ steps.&lt;id&gt;.results }</c>.
/// </summary>
public sealed class MetaPostMetricsOutput
{
    /// <summary>One result per id asked for (after de-duplication), in order.</summary>
    public IReadOnlyList<PostMetricsResult> Results { get; init; } = [];
}
