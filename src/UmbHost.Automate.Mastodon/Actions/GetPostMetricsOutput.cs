using UmbHost.Automate.Social;

namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Output of the <see cref="GetPostMetricsAction"/>, bindable as <c>${ steps.&lt;id&gt;.results }</c>.
/// </summary>
public sealed class GetPostMetricsOutput
{
    /// <summary>One result per id asked for (after de-duplication), in order.</summary>
    public IReadOnlyList<PostMetricsResult> Results { get; init; } = [];
}
