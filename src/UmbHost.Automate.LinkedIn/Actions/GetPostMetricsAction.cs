using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.LinkedIn.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Reads a batch of a company page's posts' lifetime share statistics under LinkedIn's own names.
/// </summary>
/// <remarks>
/// <para>
/// Off until LinkedIn approves the app: <c>Umbraco:Automate:Providers:LinkedIn:Insights</c> (default false; on only for "true", in any
/// case). While it's off nothing goes to LinkedIn, not even a token fetch, and every id is <c>needsPermission</c> with
/// <c>insights_off</c>. Turning it on also adds the <c>rw_organization_admin</c> scope, so existing connections must be re-authorised.
/// </para>
/// <para>
/// <c>GET /rest/organizationalEntityShareStatistics?q=organizationalEntity&amp;organizationalEntity={org}</c> with
/// <c>shares=List(…)</c> for share URNs and <c>ugcPosts[0]=…</c> for UGC post URNs, each in the form its sample shows, needing
/// <c>rw_organization_admin</c> and an ADMINISTRATOR member:
/// https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/share-statistics. Values from
/// <c>totalShareStatistics</c>: <c>impressionCount</c>, <c>uniqueImpressionsCount</c>, <c>clickCount</c>, <c>likeCount</c>,
/// <c>commentCount</c>, <c>shareCount</c>, <c>engagement</c>. Organic only, lifetime only, within a rolling 12 months.
/// </para>
/// <para>
/// "Shares that are not returned in the list of elements can be assumed to have counts of 0 for all statistics" (same page), so an
/// omitted post is <c>ok</c> with zeroes. A deleted post can't be told apart from one nobody has seen: it reads as zeroes too.
/// <b>UNVERIFIED:</b> whether <c>r_organization_admin</c> alone is enough (the docs list only <c>rw_organization_admin</c>, so we ask for
/// that), how many URNs one call takes (not documented; we send at most 20 per call), and the <c>ugcPosts[0]=</c> form with the
/// Rest.li 2.0 header (as the sample shows it, but not run live). A 403 makes that call's posts <c>needsPermission</c> /
/// <c>scope_missing</c>; any other 4xx but 401, 408 and 429 makes them <c>unavailable</c> / <c>rejected</c>, and the batch carries on.
/// </para>
/// </remarks>
[Action("linkedin.getPostMetrics", "Get LinkedIn Post Metrics",
    Description = "Reads your company page posts' impressions, clicks, likes, comments, reposts and engagement.",
    Group = "Social",
    Icon = "icon-linkedin",
    ConnectionTypeAlias = "linkedin")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "LinkedIn";

    /// <summary>The provider's insights switch (default false), beside LinkedIn's other keys.</summary>
    internal const string InsightsSetting = "Umbraco:Automate:Providers:LinkedIn:Insights";

    /// <summary>LinkedIn documents no maximum, so calls are kept small (UNVERIFIED cap).</summary>
    private const int MaxUrnsPerCall = 20;

    private const string SharePrefix = "urn:li:share:";

    private static readonly string[] Names =
        ["impressionCount", "uniqueImpressionsCount", "clickCount", "likeCount", "commentCount", "shareCount", "engagement"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;
    private readonly IConfiguration _configuration;

    public GetPostMetricsAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOAuthCredentialsService credentialsService,
        IConfiguration configuration)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
        _configuration = configuration;
    }

    // A share or UGC post URN with a numeric id, no leading zero. \z, not $, which would also match before a trailing newline.
    [GeneratedRegex(@"^urn:li:(share|ugcPost):[1-9][0-9]{0,29}\z")]
    private static partial Regex PostUrn();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<GetPostMetricsSettings>();
        var ids = PostMetricsIds.Parse(settings.Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        if (!InsightsSwitch.IsOn(_configuration, InsightsSetting))
        {
            return Success(new GetPostMetricsOutput
            {
                Results = ids.Select(id => PostMetricsResult.NeedsPermission(id, PostMetricsReason.InsightsOff)).ToList(),
            });
        }

        if (!long.TryParse(settings.OrganizationId, out var organizationId) || organizationId <= 0)
        {
            return ActionResult.Failed(new ArgumentException("Organization ID must be the page's numeric id."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<LinkedInConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A LinkedIn connection is required."), StepRunErrorCategory.Authentication);
        }

        // Ids not settled here are gone: only malformed ones (never sent). Omitted ones are settled as zeroes.
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        var valid = ids.Where(id => PostUrn().IsMatch(id)).ToList();
        if (valid.Count > 0)
        {
            string? token;
            try
            {
                token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
            }
            catch (Exception ex) when (ReadOutcome.IsTransport(ex))
            {
                return ReadOutcome.FromException(ex, cancellationToken);
            }

            if (string.IsNullOrEmpty(token))
            {
                return ActionResult.Failed(
                    new UnauthorizedAccessException("The LinkedIn token has expired (they last 60 days). Re-authorise the connection."),
                    StepRunErrorCategory.Authentication);
            }

            using var client = LinkedInClient.Create(_httpClientFactory, token, conn.ApiVersion);
            var organization = Uri.EscapeDataString($"urn:li:organization:{organizationId}");
            var shares = valid.Where(id => id.StartsWith(SharePrefix, StringComparison.Ordinal)).ToList();
            var ugcPosts = valid.Where(id => !id.StartsWith(SharePrefix, StringComparison.Ordinal)).ToList();

            foreach (var (chunk, posts) in shares.Chunk(MaxUrnsPerCall).Select(c => (c, $"shares=List({string.Join(",", c.Select(Uri.EscapeDataString))})"))
                         .Concat(ugcPosts.Chunk(MaxUrnsPerCall).Select(c => (c, string.Join("&", c.Select((urn, i) => $"ugcPosts[{i}]={Uri.EscapeDataString(urn)}"))))))
            {
                var failure = await ReadAsync(
                    context, client, $"organizationalEntityShareStatistics?q=organizationalEntity&organizationalEntity={organization}&{posts}",
                    chunk, settled, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new GetPostMetricsOutput { Results = results });
    }

    /// <summary>Reads one call's posts into <paramref name="settled"/>; returns a step failure, or null.</summary>
    private static async Task<ActionResult?> ReadAsync(
        ActionContext context, HttpClient client, string path, IReadOnlyCollection<string> chunk, Dictionary<string, PostMetricsResult> settled, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                foreach (var urn in chunk)
                {
                    settled[urn] = PostMetricsResult.NeedsPermission(urn, PostMetricsReason.ScopeMissing);
                }

                return null;
            }

            // Any other 4xx settles this call's posts alone (e.g. if LinkedIn refuses the ugcPosts[n]= form): the batch carries on.
            if (ReadOutcome.RejectsThePost(response.StatusCode))
            {
                foreach (var urn in chunk)
                {
                    settled[urn] = PostMetricsResult.Unavailable(urn, PostMetricsReason.Rejected);
                }

                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return ReadOutcome.FromResponse(context, Network, response.StatusCode, body);
            }
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
            {
                var urn = element.TryGetProperty("share", out var share) ? share.GetString()
                    : element.TryGetProperty("ugcPost", out var ugcPost) ? ugcPost.GetString()
                    : null;
                if (urn is null || !chunk.Contains(urn) || !element.TryGetProperty("totalShareStatistics", out var stats))
                {
                    continue;
                }

                settled[urn] = PostMetricsResult.Ok(urn, ReadOutcome.Counts(stats, Names));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);
        }

        // Left out: no activity, so zero for every statistic.
        foreach (var urn in chunk.Where(urn => !settled.ContainsKey(urn)))
        {
            settled[urn] = PostMetricsResult.Ok(urn, Names.ToDictionary(name => name, _ => 0m, StringComparer.Ordinal));
        }

        return null;
    }
}
