using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;
using UmbHost.Automate.Threads.Connection;

namespace UmbHost.Automate.Threads.Actions;

/// <summary>
/// Reads a batch of Threads posts' insights under Threads' own names, one call per post.
/// </summary>
/// <remarks>
/// <para>
/// Off until Meta approves the app for <c>threads_manage_insights</c>: <c>Umbraco:Automate:Providers:Threads:Insights</c> (default false;
/// on only for "true", in any case). While it's off nothing goes to Threads, not even a token refresh, and every id is
/// <c>needsPermission</c> with <c>insights_off</c>. Turning it on adds the scope to new authorisations; existing connections must be
/// authorised again, and until they are their posts come back <c>needsPermission</c> / <c>scope_missing</c>.
/// </para>
/// <para>
/// <c>GET /v1.0/{threads-media-id}/insights?metric=views,likes,replies,reposts,quotes,shares</c>, needing <c>threads_basic</c> and
/// <c>threads_manage_insights</c>: https://developers.facebook.com/docs/threads/insights. Values are <c>data[].values[0].value</c> under
/// each <c>name</c>. <c>views</c> and <c>shares</c> are "in development"; nested replies aren't counted, so this reads a thread's root
/// post only. <c>REPOST_FACADE</c> posts "return an empty array", which is <c>unavailable</c> / <c>unsupported</c>. The data delay and
/// retention aren't stated (<b>UNVERIFIED</b>). Errors are classified by <see cref="GraphError"/> (its UNVERIFIED codes included); a 4xx
/// it doesn't otherwise classify makes that post <c>unavailable</c> / <c>rejected</c> and the rest of the batch carries on.
/// </para>
/// </remarks>
[Action("threads.getPostMetrics", "Get Threads Post Metrics",
    Description = "Reads your Threads posts' views, likes, replies, reposts, quotes and shares.",
    Group = "Social",
    Icon = "icon-umbsocial-threads",
    ConnectionTypeAlias = "threads")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "Threads";
    private const string Graph = "https://graph.threads.com/v1.0/";

    /// <summary>The provider's insights switch (default false), beside Threads' other keys.</summary>
    internal const string InsightsSetting = "Umbraco:Automate:Providers:Threads:Insights";

    private const string Metrics = "views,likes,replies,reposts,quotes,shares";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ThreadsTokens _tokens;
    private readonly IConfiguration _configuration;

    public GetPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, ThreadsTokens tokens, IConfiguration configuration)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = tokens;
        _configuration = configuration;
    }

    // A numeric media id, no leading zero. \z, not $, which would also match before a trailing newline.
    [GeneratedRegex(@"^[1-9][0-9]{0,29}\z")]
    private static partial Regex MediaId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetPostMetricsSettings>().Ids);
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

        var conn = context.Connection?.GetSettings<ThreadsConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A Threads connection is required."), StepRunErrorCategory.Authentication);
        }

        // Ids not settled here are gone: malformed ones (never sent).
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        var valid = ids.Where(id => MediaId().IsMatch(id)).ToList();
        if (valid.Count > 0)
        {
            string? token;
            try
            {
                token = await _tokens.GetAsync(credentialsId, cancellationToken);
            }
            catch (Exception ex) when (ReadOutcome.IsTransport(ex))
            {
                return ReadOutcome.FromException(ex, cancellationToken);
            }

            if (string.IsNullOrEmpty(token))
            {
                return ActionResult.Failed(
                    new UnauthorizedAccessException("The Threads token has expired or was revoked. Re-authorise the connection."),
                    StepRunErrorCategory.Authentication);
            }

            using var client = _httpClientFactory.CreateClient();
            foreach (var id in valid)
            {
                var failure = await ReadAsync(client, id, token, settled, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new GetPostMetricsOutput { Results = results });
    }

    /// <summary>Reads one post into <paramref name="settled"/>; returns a step failure, or null.</summary>
    private static async Task<ActionResult?> ReadAsync(
        HttpClient client, string id, string token, Dictionary<string, PostMetricsResult> settled, CancellationToken cancellationToken)
    {
        string body;
        try
        {
            // access_token as a query parameter, as the Threads reference shows.
            using var response = await client.GetAsync(
                $"{Graph}{id}/insights?metric={Metrics}&access_token={Uri.EscapeDataString(token)}", cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var kind = GraphError.Classify(response.StatusCode, body);
                switch (kind)
                {
                    case GraphError.Kind.Permission:
                        settled[id] = PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing);
                        return null;
                    case GraphError.Kind.NotFound:
                        settled[id] = PostMetricsResult.Gone(id);
                        return null;
                    case GraphError.Kind.Rejected:
                        settled[id] = PostMetricsResult.Unavailable(id, PostMetricsReason.Rejected);
                        return null;
                    default:
                        return GraphError.Fail(Network, kind, response.StatusCode, body);
                }
            }
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }

        try
        {
            var values = GraphInsights.Read(body);
            settled[id] = values.Count == 0
                ? PostMetricsResult.Unavailable(id, PostMetricsReason.Unsupported)
                : PostMetricsResult.Ok(id, values);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);
        }
    }
}
