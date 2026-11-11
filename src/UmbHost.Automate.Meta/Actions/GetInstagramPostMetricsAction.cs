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
/// Reads a batch of Instagram posts' lifetime insights under Instagram's own names, one call per post.
/// </summary>
/// <remarks>
/// <para>
/// Off until Meta approves the app for <c>instagram_manage_insights</c>: <c>Umbraco:Automate:Providers:Meta:Insights</c> (default false;
/// on only for "true", in any case), shared with the Facebook metrics. While it's off nothing goes to Meta and every id is
/// <c>needsPermission</c> with <c>insights_off</c>.
/// </para>
/// <para>
/// <c>GET /{ig-media-id}/insights?metric=views,reach,likes,comments,shares,saved,reposts&amp;period=lifetime</c>, all seven valid for
/// feed posts and reels, needing <c>instagram_basic</c>, <c>instagram_manage_insights</c> and <c>pages_read_engagement</c>; data can be
/// delayed up to 48 hours: https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights. An answer with no
/// metrics at all is <c>needsPermission</c> / <c>scope_missing</c>, as Facebook's empty dataset.
/// </para>
/// <para>
/// "Insights data is not available for any media within an Instagram Media album" (same page). What the call answers for an album is
/// <b>UNVERIFIED</b>, so when insights fails with an error <see cref="GraphError"/> doesn't classify, the media is looked up
/// (<c>GET /{ig-media-id}?fields=media_type</c>; values <c>CAROUSEL_ALBUM</c>, <c>IMAGE</c>, <c>VIDEO</c>:
/// https://developers.facebook.com/docs/instagram-platform/reference/instagram-media). An album is <c>unavailable</c> /
/// <c>unsupported</c>; anything else fails the step with the original error.
/// </para>
/// </remarks>
[Action("meta.getInstagramPostMetrics", "Get Instagram Post Metrics",
    Description = "Reads your Instagram posts' views, reach, likes, comments, shares, saves and reposts.",
    Group = "Social",
    Icon = "icon-picture",
    ConnectionTypeAlias = "meta")]
public sealed partial class GetInstagramPostMetricsAction : ActionBase<GetInstagramPostMetricsSettings, MetaPostMetricsOutput>
{
    private const string Network = "Instagram";
    private const string Metrics = "views,reach,likes,comments,shares,saved,reposts";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public GetInstagramPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    // A numeric media id, no leading zero. \z, not $, which would also match before a trailing newline.
    [GeneratedRegex(@"^[1-9][0-9]{0,29}\z")]
    private static partial Regex MediaId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetInstagramPostMetricsSettings>().Ids);
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
        foreach (var id in ids.Where(id => MediaId().IsMatch(id)))
        {
            var insights = await GraphRead.GetAsync(client, Network, $"{id}/insights?metric={Metrics}&period=lifetime", cancellationToken, keepOther: true);
            if (insights.Failure is not null)
            {
                return insights.Failure;
            }

            if (insights.Settle(id) is { } result)
            {
                settled[id] = result;
                continue;
            }

            if (insights.IsOther)
            {
                if (!await IsAlbumAsync(client, id, cancellationToken))
                {
                    return cancellationToken.IsCancellationRequested
                        ? ActionResult.Failed(new OperationCanceledException(cancellationToken), StepRunErrorCategory.Cancelled)
                        : GraphError.Fail(Network, GraphError.Kind.Other, insights.FailedStatus, insights.FailedBody);
                }

                settled[id] = PostMetricsResult.Unavailable(id, PostMetricsReason.Unsupported);
                continue;
            }

            try
            {
                var values = GraphInsights.Read(insights.Body!);
                settled[id] = values.Count == 0
                    ? PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing)
                    : PostMetricsResult.Ok(id, values);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);
            }
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var r) ? r : PostMetricsResult.Gone(id)).ToList();
        return Success(new MetaPostMetricsOutput { Results = results });
    }

    /// <summary>Whether the media is an album. Any failure to tell is "no", so the caller reports the original error.</summary>
    private static async Task<bool> IsAlbumAsync(HttpClient client, string id, CancellationToken cancellationToken)
    {
        var lookup = await GraphRead.GetAsync(client, Network, $"{id}?fields=media_type", cancellationToken, keepOther: true);
        if (lookup.Body is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(lookup.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("media_type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.GetString() == "CAROUSEL_ALBUM";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
