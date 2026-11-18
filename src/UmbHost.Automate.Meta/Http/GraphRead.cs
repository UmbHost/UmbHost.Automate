using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Http;

/// <summary>
/// One Graph read for one post: its body on success; the post's own result when the error belongs to that post (a missing permission,
/// a post that's gone, any other 4xx); otherwise a step failure (the token, throttling, 5xx, transport).
/// </summary>
internal sealed class GraphRead
{
    private GraphRead()
    {
    }

    /// <summary>The answer's body, when the read succeeded.</summary>
    public string? Body { get; private init; }

    /// <summary>The step failure, when the error is the whole step's.</summary>
    public ActionResult? Failure { get; private init; }

    /// <summary>Whether the read was refused with a 4xx no other result covers (<see cref="GraphError.Kind.Rejected"/>).</summary>
    public bool IsRejected => PostError == GraphError.Kind.Rejected;

    private GraphError.Kind? PostError { get; init; }

    /// <summary>The post's result when this read settled it (needs permission, gone, or rejected); otherwise null.</summary>
    public PostMetricsResult? Settle(string id) => PostError switch
    {
        GraphError.Kind.Permission => PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing),
        GraphError.Kind.NotFound => PostMetricsResult.Gone(id),
        GraphError.Kind.Rejected => PostMetricsResult.Unavailable(id, PostMetricsReason.Rejected),
        _ => null,
    };

    /// <summary>GETs <paramref name="path"/> and classifies a failed answer with <see cref="GraphError"/>.</summary>
    public static async Task<GraphRead> GetAsync(ActionContext context, HttpClient client, string network, string path, CancellationToken cancellationToken)
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
            return kind is GraphError.Kind.Permission or GraphError.Kind.NotFound or GraphError.Kind.Rejected
                ? new GraphRead { PostError = kind }
                : new GraphRead { Failure = GraphError.Fail(context, network, kind, response.StatusCode, body) };
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return new GraphRead { Failure = ReadOutcome.FromException(ex, cancellationToken) };
        }
    }
}
