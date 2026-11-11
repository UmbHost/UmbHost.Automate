using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Connection;
using UmbHost.Automate.Mastodon.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Reads a batch of statuses' public counts (favourites, boosts, replies and, from 4.5.0, quotes) under Mastodon's own names,
/// from the instance the connection posts to.
/// </summary>
/// <remarks>
/// <para>
/// The read sends no token: the connection's token has only <c>write:*</c> scopes. Public and unlisted statuses need none
/// (https://docs.joinmastodon.org/methods/statuses/; unlisted per
/// https://github.com/mastodon/mastodon/blob/main/app/policies/status_policy.rb). Without a token Mastodon answers a private
/// status exactly as a deleted one (404, "does not exist or is private"; the batch route leaves both out), so both are
/// <c>gone</c>. An instance in authorized-fetch mode answers 401 (or 403), which is <c>unavailable</c> with <c>scope_missing</c>.
/// A redirect is never followed (the instance must be the real https host): it is <c>unavailable</c> with <c>redirected</c>.
/// </para>
/// <para>
/// <c>GET /api/v1/statuses?id[]=</c> (4.3.0+) takes at most 20 ids: <c>DEFAULT_STATUSES_LIMIT</c> in
/// https://github.com/mastodon/mastodon/blob/main/app/controllers/api/base_controller.rb, enforced by <c>check_statuses_limit</c> in
/// https://github.com/mastodon/mastodon/blob/main/app/controllers/api/v1/statuses_controller.rb. An older instance answers the
/// batch route with 404, and the read falls back to <c>GET /api/v1/statuses/:id</c> one at a time.
/// Counts: https://docs.joinmastodon.org/entities/Status/.
/// </para>
/// </remarks>
[Action("mastodon.getPostMetrics", "Get Mastodon Post Metrics",
    Description = "Reads statuses' public favourite, boost, reply and quote counts.",
    Group = "Social",
    Icon = "icon-chart-curve",
    ConnectionTypeAlias = "mastodon")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "Mastodon";
    private const int MaxIdsPerCall = 20;

    private static readonly string[] CountNames = ["favourites_count", "reblogs_count", "replies_count", "quotes_count"];

    private readonly IHttpClientFactory _httpClientFactory;

    public GetPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
        => _httpClientFactory = httpClientFactory;

    // Mastodon ids are decimal snowflakes with no leading zero: "0123" is refused rather than sent as a different id.
    [GeneratedRegex(@"^[1-9][0-9]{0,29}$")]
    private static partial Regex StatusId();

    /// <summary>Why an answer leaves the ids it covers unavailable (auth required, or a redirect), or null.</summary>
    private static string? Unavailable(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? PostMetricsReason.ScopeMissing
            : ReadOutcome.IsRedirect(status) ? PostMetricsReason.Redirected
            : null;

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetPostMetricsSettings>().Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        var conn = context.Connection?.GetSettings<MastodonConnectionSettings>();
        if (conn is null || !Uri.TryCreate(conn.InstanceUrl, UriKind.Absolute, out var instance) || instance.Scheme != Uri.UriSchemeHttps)
        {
            return ActionResult.Failed(
                new InvalidOperationException("A Mastodon connection with an https instance URL is required."),
                StepRunErrorCategory.ConfigurationError);
        }

        using var client = MastodonClient.CreatePublic(_httpClientFactory, conn);

        // Ids not settled here are gone: malformed (never sent), deleted, or private.
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        var batchRoute = true;

        try
        {
            foreach (var chunk in ids.Where(id => StatusId().IsMatch(id)).Chunk(MaxIdsPerCall))
            {
                if (batchRoute)
                {
                    var query = string.Join("&", chunk.Select(id => "id[]=" + id));
                    using var response = await client.GetAsync($"v1/statuses?{query}", cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(body);
                        foreach (var status in doc.RootElement.EnumerateArray())
                        {
                            if (status.GetProperty("id").GetString() is { } id && chunk.Contains(id))
                            {
                                settled[id] = PostMetricsResult.Ok(id, ReadOutcome.Counts(status, CountNames));
                            }
                        }

                        continue;
                    }

                    if (Unavailable(response.StatusCode) is { } reason)
                    {
                        foreach (var id in chunk)
                        {
                            settled[id] = PostMetricsResult.Unavailable(id, reason);
                        }

                        continue;
                    }

                    if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        return ReadOutcome.FromResponse(Network, response.StatusCode, body);
                    }

                    // Before 4.3.0 there is no batch route: read this chunk and the rest one status at a time.
                    batchRoute = false;
                }

                foreach (var id in chunk)
                {
                    using var response = await client.GetAsync($"v1/statuses/{id}", cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(body);
                        settled[id] = PostMetricsResult.Ok(id, ReadOutcome.Counts(doc.RootElement, CountNames));
                    }
                    else if (Unavailable(response.StatusCode) is { } reason)
                    {
                        settled[id] = PostMetricsResult.Unavailable(id, reason);
                    }
                    else if (response.StatusCode != HttpStatusCode.NotFound)
                    {
                        return ReadOutcome.FromResponse(Network, response.StatusCode, body);
                    }
                }
            }
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new GetPostMetricsOutput { Results = results });
    }
}
