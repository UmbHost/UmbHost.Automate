using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Configuration;
using UmbHost.Automate.Meta.Connection;
using UmbHost.Automate.Meta.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Reads a batch of Facebook Page posts' views, reactions, comments and shares, two calls per post.
/// </summary>
/// <remarks>
/// <para>
/// Off until Meta approves the app: <c>Umbraco:Automate:Providers:Meta:Insights</c> (default false; on only for "true", in any case),
/// shared with the Instagram metrics. While it's off nothing goes to Meta and every id is <c>needsPermission</c> with <c>insights_off</c>.
/// </para>
/// <para>
/// Counts: <c>GET /{post-id}?fields=shares,comments.summary(true).limit(0),reactions.summary(true).limit(0)</c>, read as
/// <c>shares</c> (<c>shares.count</c>: https://developers.facebook.com/docs/graph-api/reference/post/), <c>reactions</c>
/// (<c>reactions.summary.total_count</c>: https://developers.facebook.com/docs/graph-api/reference/object/reactions/) and
/// <c>comments</c> (<c>comments.summary.total_count</c>: https://developers.facebook.com/docs/graph-api/reference/object/comments/).
/// <b>UNVERIFIED:</b> whether a post nobody has shared omits <c>shares</c>; if it does, <c>shares</c> is left out rather than reported as 0.
/// Also <b>UNVERIFIED:</b> that these counts need <c>pages_read_user_content</c> (from Meta's permissions reference, not the pages above,
/// which name <c>pages_read_engagement</c>).
/// </para>
/// <para>
/// Views: <c>GET /{post-id}/insights?metric=post_media_view</c>, the documented replacement for <c>post_impressions</c> (deprecated
/// 2025-11-15): https://developers.facebook.com/documentation/pages-api/platforminsights/page/deprecated-metrics.md. <b>UNVERIFIED:</b>
/// that page names it as the alternative for the <c>post_impressions*</c> rows, but the current Page insights reference doesn't list
/// <c>post_media_view</c> as a post-level metric. It needs
/// <c>read_insights</c> and <c>pages_read_engagement</c>; without <c>read_insights</c> "An empty dataset is returned"
/// (https://developers.facebook.com/documentation/pages-api/platforminsights/page.md), which is <c>needsPermission</c> /
/// <c>scope_missing</c>. <c>post_clicks</c> isn't read: the current docs no longer list it.
/// </para>
/// <para>
/// A post is <c>ok</c> only when both calls succeed. Errors are classified by <see cref="GraphError"/> (its UNVERIFIED codes included): a
/// 4xx it doesn't otherwise classify makes that post <c>unavailable</c> / <c>rejected</c> and the rest of the batch carries on.
/// </para>
/// </remarks>
[Action("meta.getFacebookPostMetrics", "Get Facebook Post Metrics",
    Description = "Reads your Facebook Page posts' views, reactions, comments and shares.",
    Group = "Social",
    Icon = "icon-facebook",
    ConnectionTypeAlias = "meta")]
public sealed partial class GetFacebookPostMetricsAction : ActionBase<GetFacebookPostMetricsSettings, MetaPostMetricsOutput>
{
    private const string Network = "Facebook";
    private const string CountFields = "shares,comments.summary(true).limit(0),reactions.summary(true).limit(0)";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GetFacebookPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    // {page-id}_{post-id}, as the Create Facebook Page Post action records it. \z, not $, which would also match before a trailing newline.
    [GeneratedRegex(@"^[1-9][0-9]{0,29}_[1-9][0-9]{0,29}\z")]
    private static partial Regex PostId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetFacebookPostMetricsSettings>().Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        if (!InsightsSwitch.IsOn(_configuration, MetaInsights.Setting))
        {
            return Success(new MetaPostMetricsOutput
            {
                Results = ids.Select(id => PostMetricsResult.NeedsPermission(id, PostMetricsReason.InsightsOff)).ToList(),
            });
        }

        var conn = context.Connection?.GetSettings<MetaConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.PageAccessToken))
        {
            return ActionResult.Failed(new InvalidOperationException("A Facebook & Instagram connection is required."), StepRunErrorCategory.Authentication);
        }

        // Ids not settled here are gone: malformed ones (never sent).
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        using var client = GraphClient.Create(_httpClientFactory, conn);
        foreach (var id in ids.Where(id => PostId().IsMatch(id)))
        {
            var counts = await GraphRead.GetAsync(client, Network, $"{id}?fields={CountFields}", cancellationToken);
            if (counts.Failure is not null)
            {
                return counts.Failure;
            }

            if (counts.Settle(id) is { } countsResult)
            {
                settled[id] = countsResult;
                continue;
            }

            Dictionary<string, decimal> values;
            try
            {
                values = Counts(counts.Body!);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Unreadable(ex);
            }

            var views = await GraphRead.GetAsync(client, Network, $"{id}/insights?metric=post_media_view", cancellationToken);
            if (views.Failure is not null)
            {
                return views.Failure;
            }

            if (views.Settle(id) is { } viewsResult)
            {
                settled[id] = viewsResult;
                continue;
            }

            IReadOnlyDictionary<string, decimal> insights;
            try
            {
                insights = GraphInsights.Read(views.Body!);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return Unreadable(ex);
            }

            if (insights.Count == 0)
            {
                // No read_insights: an empty dataset, not an error. No half results: the whole post needs permission.
                settled[id] = PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing);
                continue;
            }

            foreach (var (name, value) in insights)
            {
                values[name] = value;
            }

            settled[id] = PostMetricsResult.Ok(id, values);
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new MetaPostMetricsOutput { Results = results });
    }

    private static ActionResult Unreadable(Exception ex)
        => ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);

    /// <summary>The post's <c>reactions</c>, <c>comments</c> and <c>shares</c>, each when present.</summary>
    private static Dictionary<string, decimal> Counts(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var post = doc.RootElement;
        if (post.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The post isn't an object.");
        }

        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        Add(values, "reactions", post, "reactions", "summary", "total_count");
        Add(values, "comments", post, "comments", "summary", "total_count");
        Add(values, "shares", post, "shares", "count");
        return values;
    }

    /// <summary>Adds the number at <paramref name="path"/> under <paramref name="name"/>, if it is there.</summary>
    private static void Add(Dictionary<string, decimal> values, string name, JsonElement element, params string[] path)
    {
        foreach (var step in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(step, out element))
            {
                return;
            }
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
        {
            values[name] = number;
        }
    }
}
