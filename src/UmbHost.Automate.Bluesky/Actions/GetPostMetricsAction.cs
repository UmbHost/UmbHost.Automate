using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Actions;

/// <summary>
/// Reads a batch of posts' public counts (likes, reposts, replies, quotes and bookmarks) under Bluesky's own names.
/// </summary>
/// <remarks>
/// <para>
/// The connection is declared because UmbSocial runs one metrics automation per channel and the connection picks the channel.
/// The read itself is public and unauthenticated: it goes to the public AppView and never signs in or uses the app password.
/// </para>
/// <para>
/// <c>app.bsky.feed.getPosts</c> takes at most 25 at-uris per call (<c>uris</c> maxLength 25):
/// https://github.com/bluesky-social/atproto/blob/main/packages/api/lexicons/app/bsky/feed/getPosts.json.
/// Counts are PostView fields: https://github.com/bluesky-social/atproto/blob/main/packages/api/lexicons/app/bsky/feed/defs.json.
/// A uri the AppView doesn't return (deleted, or never existed) is <c>gone</c>.
/// An at-uri naming a handle is resolved to a DID first with <c>com.atproto.identity.resolveHandle</c>:
/// https://github.com/bluesky-social/atproto/blob/main/lexicons/com/atproto/identity/resolveHandle.json.
/// </para>
/// </remarks>
[Action("bluesky.getPostMetrics", "Get Bluesky Post Metrics",
    Description = "Reads posts' public like, repost, reply, quote and bookmark counts.",
    Group = "Social",
    Icon = "icon-chart-curve",
    ConnectionTypeAlias = "bluesky")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "Bluesky";
    private const int MaxUrisPerCall = 25;

    private static readonly string[] CountNames = ["likeCount", "repostCount", "replyCount", "quoteCount", "bookmarkCount"];

    private readonly IHttpClientFactory _httpClientFactory;

    public GetPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
        => _httpClientFactory = httpClientFactory;

    [GeneratedRegex(@"^at://([^/?#\s]+)/app\.bsky\.feed\.post/([A-Za-z0-9._:~-]{1,512})\z")]
    private static partial Regex PostUri();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetPostMetricsSettings>().Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        using var client = BlueskyClient.CreatePublic(_httpClientFactory);

        try
        {
            // Each id's at-uri in DID form, or null when it is malformed or its handle doesn't resolve (gone).
            var canonical = new Dictionary<string, string?>(StringComparer.Ordinal);
            var dids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var match = PostUri().Match(id);
                if (!match.Success)
                {
                    canonical[id] = null;
                    continue;
                }

                var authority = match.Groups[1].Value;
                if (!authority.StartsWith("did:", StringComparison.Ordinal))
                {
                    if (!dids.TryGetValue(authority, out var did))
                    {
                        var (resolved, failure) = await ResolveHandleAsync(context, client, authority, cancellationToken);
                        if (failure is not null)
                        {
                            return failure;
                        }

                        dids[authority] = did = resolved;
                    }

                    authority = did;
                }

                canonical[id] = authority is null ? null : $"at://{authority}/app.bsky.feed.post/{match.Groups[2].Value}";
            }

            var found = new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.Ordinal);
            foreach (var chunk in canonical.Values.OfType<string>().Distinct(StringComparer.Ordinal).Chunk(MaxUrisPerCall))
            {
                var query = string.Join("&", chunk.Select(u => "uris=" + Uri.EscapeDataString(u)));
                using var response = await client.GetAsync($"app.bsky.feed.getPosts?{query}", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return ReadOutcome.FromResponse(context, Network, response.StatusCode, body);
                }

                using var doc = JsonDocument.Parse(body);
                foreach (var post in doc.RootElement.GetProperty("posts").EnumerateArray())
                {
                    if (post.TryGetProperty("uri", out var uri) && uri.GetString() is { } u)
                    {
                        found[u] = ReadOutcome.Counts(post, CountNames);
                    }
                }
            }

            var results = ids.Select(id => canonical[id] is { } uri && found.TryGetValue(uri, out var values)
                ? PostMetricsResult.Ok(id, values)
                : PostMetricsResult.Gone(id)).ToList();

            return Success(new GetPostMetricsOutput { Results = results });
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);
        }
    }

    /// <summary>Resolves a handle to its DID; a handle that doesn't resolve (400, e.g. HandleNotFound) is null.</summary>
    private static async Task<(string? Did, ActionResult? Failure)> ResolveHandleAsync(ActionContext context, HttpClient client, string handle, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"com.atproto.identity.resolveHandle?handle={Uri.EscapeDataString(handle)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return (null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, ReadOutcome.FromResponse(context, Network, response.StatusCode, body));
        }

        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("did").GetString(), null);
    }
}
