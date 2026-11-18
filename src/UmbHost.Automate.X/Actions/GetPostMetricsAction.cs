using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Social;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Actions;

/// <summary>
/// Reads a batch of the connected account's posts' metrics under X's own names: the public counts, plus link and profile clicks
/// while X still returns them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read costs money.</b> X bills "$0.005 per resource" for Post reads, per Post returned rather than per request. Re-reading
/// a Post in the same UTC day isn't charged again (a "soft guarantee"), and reads are capped at 3 million Posts a month:
/// https://docs.x.com/x-api/getting-started/pricing. So reads happen only when two switches are on: this provider's
/// <c>Umbraco:Automate:Providers:Twitter:Insights</c> (default false; while it's off nothing goes to X and every id is
/// <c>needsPermission</c> with <c>insights_off</c>), and, for UmbSocial, X metrics in Social › Settings (UmbSocial doesn't run this
/// action while that is off).
/// </para>
/// <para>
/// <c>GET /2/tweets?ids=</c> takes 1 to 100 comma-separated ids (<c>^[0-9]{1,19}$</c>) and needs <c>tweet.read</c> and
/// <c>users.read</c>, which the connection already has: https://docs.x.com/x-api/posts/get-posts-by-ids.
/// Values: <c>public_metrics</c> (<c>impression_count</c>, <c>like_count</c>, <c>retweet_count</c>, <c>reply_count</c>,
/// <c>quote_count</c>, <c>bookmark_count</c>), plus <c>url_link_clicks</c> and <c>user_profile_clicks</c> from
/// <c>non_public_metrics</c> or <c>organic_metrics</c>, which X returns only for posts created in the last 30 days:
/// https://docs.x.com/x-api/fundamentals/metrics.
/// </para>
/// <para>
/// A 200 can carry both <c>data</c> and <c>errors</c>: https://docs.x.com/x-api/fundamentals/response-codes-and-errors. An id in
/// <c>errors</c> as <c>resource-not-found</c> is <c>gone</c>; one withheld for another reason (e.g. <c>not-authorized-for-resource</c>)
/// is <c>unavailable</c> with <c>scope_missing</c>; one left out with no error is <c>gone</c>. An error entry for an id that is in
/// <c>data</c> (for example about a metrics field) doesn't change that post's result.
/// </para>
/// </remarks>
[Action("x.getPostMetrics", "Get X Post Metrics",
    Description = "Reads your posts' impressions, likes, reposts, replies, quotes, bookmarks and clicks. Each read is billed by X.",
    Group = "Social",
    Icon = "icon-chart-curve",
    ConnectionTypeAlias = "x")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "X";

    /// <summary>The provider's X metrics switch (bool, default false), beside X's other keys under <c>Providers:Twitter</c>.</summary>
    internal const string InsightsSetting = "Umbraco:Automate:Providers:Twitter:Insights";
    private const int MaxIdsPerCall = 100;
    private const string Fields = "public_metrics,non_public_metrics,organic_metrics";
    // Matched on the path: the problem type's host has been api.twitter.com as well as api.x.com.
    private const string NotFoundPath = "/problems/resource-not-found";

    private static readonly string[] PublicNames =
        ["impression_count", "like_count", "retweet_count", "reply_count", "quote_count", "bookmark_count"];

    private static readonly string[] OwnerNames = ["url_link_clicks", "user_profile_clicks"];

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
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    // X's PostId pattern, without a leading zero (which would name a different post to us than to X).
    [GeneratedRegex(@"^[1-9][0-9]{0,18}\z")]
    private static partial Regex PostId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetPostMetricsSettings>().Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        // Off by default: every read is billed. Nothing goes to X while it's off, not even a token refresh.
        // Only "true" (any case) turns it on; anything else, malformed included, is off.
        if (!string.Equals(_configuration[InsightsSetting], "true", StringComparison.OrdinalIgnoreCase))
        {
            return Success(new GetPostMetricsOutput
            {
                Results = ids.Select(id => PostMetricsResult.NeedsPermission(id, PostMetricsReason.InsightsOff)).ToList(),
            });
        }

        var conn = context.Connection?.GetSettings<XConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("An X connection is required."), StepRunErrorCategory.Authentication);
        }

        // Ids not settled here are gone: malformed (never sent), not found, or left out.
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        var valid = ids.Where(id => PostId().IsMatch(id)).ToList();

        try
        {
            if (valid.Count > 0)
            {
                var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
                if (string.IsNullOrEmpty(token))
                {
                    return ActionResult.Failed(
                        new UnauthorizedAccessException("The X token is expired or revoked. Re-authorise the connection."),
                        StepRunErrorCategory.Authentication);
                }

                using var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                foreach (var chunk in valid.Chunk(MaxIdsPerCall))
                {
                    using var response = await client.GetAsync(
                        $"https://api.x.com/2/tweets?ids={string.Join(",", chunk)}&tweet.fields={Fields}", cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return ReadOutcome.FromResponse(context, Network, response.StatusCode, body);
                    }

                    try
                    {
                        Read(body, chunk, settled);
                    }
                    catch (Exception ex) when (IsUnreadable(ex))
                    {
                        return Unreadable(ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new GetPostMetricsOutput { Results = results });
    }

    /// <summary>The step failure for an answer that can't be read.</summary>
    private static ActionResult Unreadable(Exception ex)
        => ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);

    // Only the parse of a successful answer is caught as unreadable, never the token lookup or the request.
    private static bool IsUnreadable(Exception ex) => ex is JsonException or KeyNotFoundException or InvalidOperationException;

    /// <summary>Settles the chunk's ids from one answer: posts in <c>data</c> are ok; ids in <c>errors</c> are gone or unavailable.</summary>
    private static void Read(string body, IReadOnlyCollection<string> chunk, Dictionary<string, PostMetricsResult> settled)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data))
        {
            foreach (var post in data.EnumerateArray())
            {
                if (post.GetProperty("id").GetString() is not { } id || !chunk.Contains(id))
                {
                    continue;
                }

                var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
                Add(values, post, "public_metrics", PublicNames);
                Add(values, post, "organic_metrics", OwnerNames);
                Add(values, post, "non_public_metrics", OwnerNames);

                settled[id] = PostMetricsResult.Ok(id, values);
            }
        }

        if (root.TryGetProperty("errors", out var errors))
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (!error.TryGetProperty("resource_id", out var rid) || rid.GetString() is not { } id
                    || !chunk.Contains(id) || settled.ContainsKey(id))
                {
                    continue;
                }

                var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                settled[id] = type is not null && type.EndsWith(NotFoundPath, StringComparison.Ordinal)
                    ? PostMetricsResult.Gone(id)
                    : PostMetricsResult.Unavailable(id, PostMetricsReason.ScopeMissing);
            }
        }
    }

    /// <summary>Adds the named counts from one metrics block, if X sent it (owner blocks stop after 30 days).</summary>
    private static void Add(Dictionary<string, decimal> values, JsonElement post, string block, string[] names)
    {
        if (post.TryGetProperty(block, out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, value) in ReadOutcome.Counts(metrics, names))
            {
                values[name] = value;
            }
        }
    }
}
