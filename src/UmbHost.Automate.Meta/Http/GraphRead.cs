using System.Net;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Http;

/// <summary>
/// One Graph read for one post: its body on success; the post's own result when the error belongs to that post (a missing permission,
/// a post that's gone); otherwise a step failure, or, when the caller asks to look further, the unclassified error itself.
/// </summary>
internal sealed class GraphRead
{
    private GraphRead()
    {
    }

    /// <summary>The answer's body, when the read succeeded.</summary>
    public string? Body { get; private init; }

    /// <summary>The step failure, when the error is the whole step's (token, throttling, transport, or unclassified without <c>keepOther</c>).</summary>
    public ActionResult? Failure { get; private init; }

    /// <summary>True for an unclassified 4xx error kept for the caller (only with <c>keepOther</c>); see <see cref="FailedStatus"/> and <see cref="FailedBody"/>.</summary>
    public bool IsOther { get; private init; }

    /// <summary>The status of the error kept by <see cref="IsOther"/>.</summary>
    public HttpStatusCode FailedStatus { get; private init; }

    /// <summary>The body of the error kept by <see cref="IsOther"/>.</summary>
    public string FailedBody { get; private init; } = string.Empty;

    private GraphError.Kind? PostError { get; init; }

    /// <summary>The post's result when this read settled it (needs permission, or gone); otherwise null.</summary>
    public PostMetricsResult? Settle(string id) => PostError switch
    {
        GraphError.Kind.Permission => PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing),
        GraphError.Kind.NotFound => PostMetricsResult.Gone(id),
        _ => null,
    };

    /// <summary>GETs <paramref name="path"/> and classifies a failed answer with <see cref="GraphError"/>.</summary>
    public static async Task<GraphRead> GetAsync(
        HttpClient client, string network, string path, CancellationToken cancellationToken, bool keepOther = false)
    {
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new GraphRead { Body = body };
            }

            var kind = GraphError.Classify(response.StatusCode, body);
            return kind switch
            {
                GraphError.Kind.Permission or GraphError.Kind.NotFound => new GraphRead { PostError = kind },
                GraphError.Kind.Other when keepOther && (int)response.StatusCode is >= 400 and < 500 => new GraphRead { IsOther = true, FailedStatus = response.StatusCode, FailedBody = body },
                _ => new GraphRead { Failure = GraphError.Fail(network, kind, response.StatusCode, body) },
            };
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return new GraphRead { Failure = ReadOutcome.FromException(ex, cancellationToken) };
        }
    }
}
