using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// Reads posts' public counts from the Bluesky AppView.
/// Endpoint and limit (uris maxLength 25): https://github.com/bluesky-social/atproto/blob/main/packages/api/lexicons/app/bsky/feed/getPosts.json
/// PostView fields (likeCount, repostCount, replyCount, quoteCount, bookmarkCount): https://github.com/bluesky-social/atproto/blob/main/packages/api/lexicons/app/bsky/feed/defs.json
/// Handle resolution: https://github.com/bluesky-social/atproto/blob/main/lexicons/com/atproto/identity/resolveHandle.json
/// </summary>
public class GetPostMetricsActionTests
{
    private const string AppView = "https://public.api.bsky.app/xrpc/";
    private const string GetPosts = AppView + "app.bsky.feed.getPosts";
    private const string ResolveHandle = AppView + "com.atproto.identity.resolveHandle";

    private static readonly BlueskyConnectionSettings Conn = new() { Identifier = "umbhost.net", AppPassword = "app-pass" };

    private static string Uri(string rkey, string authority = "did:plc:abc") => $"at://{authority}/app.bsky.feed.post/{rkey}";

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory());

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static List<string> UrisOf(HttpRequestMessage request)
        => request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p[0] == "uris")
            .Select(p => System.Uri.UnescapeDataString(p[1]))
            .ToList();

    /// <summary>The live PostViews the AppView served for the account's posts on 2026-11-18 (Fixtures/Live/author-feed.json).</summary>
    private static List<JsonObject> LivePostViews()
        => LiveFixture.Object("author-feed")["feed"]!.AsArray().Select(i => i!["post"]!.AsObject()).ToList();

    /// <summary>A real PostView (the live image post) under another uri, with counts 1 to 5 so each name is told apart.</summary>
    private static JsonObject PostView(string uri, int likes = 1, int reposts = 2, int replies = 3, int quotes = 4, int bookmarks = 5)
    {
        var view = LivePostViews().Single(p => p["embed"]?["$type"]?.GetValue<string>() == "app.bsky.embed.images#view");
        view.Parent!.AsObject().Remove("post");
        view["uri"] = uri;
        view["bookmarkCount"] = bookmarks;
        view["replyCount"] = replies;
        view["repostCount"] = reposts;
        view["likeCount"] = likes;
        view["quoteCount"] = quotes;
        return view;
    }

    [Fact]
    public async Task live_post_views_read_back_their_real_counts()
    {
        var handler = new Mock<HttpMessageHandler>();
        var live = LivePostViews();
        foreach (var view in live)
        {
            view.Parent!.AsObject().Remove("post");
        }

        // getPosts answers {"posts":[PostView]}, the same PostView getAuthorFeed carries in each item's "post".
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.GetLeftPart(UriPartial.Path) == GetPosts)
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonObject { ["posts"] = new JsonArray(live.Select(v => (JsonNode)v).ToArray()) }.ToJsonString()));
        var uris = live.Select(v => v["uri"]!.GetValue<string>()).ToArray();

        var results = Results(await Sut(handler).ExecuteAsync(Context(uris), default));

        results.Should().HaveCount(7).And.OnlyContain(r => r.State == "ok");
        // Every live PostView carried all five counts: text, image, link-card and reply posts alike.
        results.Should().OnlyContain(r => r.Values.Count == 5);
        // The first test post had one like and the thread's first part one reply; nothing was reposted, quoted or bookmarked.
        results.Single(r => r.Id.EndsWith("/3n2jexzfz4y2d")).Values["likeCount"].Should().Be(1);
        results.Single(r => r.Id.EndsWith("/3n2jfifbun22p")).Values["replyCount"].Should().Be(1);
        results.Sum(r => r.Values["repostCount"] + r.Values["quoteCount"] + r.Values["bookmarkCount"]).Should().Be(0);
    }

    /// <summary>Answers getPosts with a PostView for each uri asked for, except those listed as missing.</summary>
    private static List<HttpRequestMessage> ServePosts(Mock<HttpMessageHandler> handler, params string[] missing)
    {
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.GetLeftPart(UriPartial.Path) == GetPosts)
               .ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
               {
                   requests.Add(r);
                   var posts = new JsonArray(UrisOf(r).Where(u => !missing.Contains(u)).Select(u => (JsonNode)PostView(u)).ToArray());
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["posts"] = posts }.ToJsonString()) };
               });
        return requests;
    }

    private static IReadOnlyList<PostMetricsResult> Results(ActionResult result)
    {
        result.Status.Should().Be(ActionResultStatus.Success);
        return result.OutputData.Should().BeOfType<GetPostMetricsOutput>().Subject.Results;
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new GetPostMetricsSettings { Ids = ids }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task bluesky_counts_come_back_under_bluesky_names()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServePosts(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context(Uri("3k2a")), default));

        var only = results.Should().ContainSingle().Subject;
        only.Id.Should().Be(Uri("3k2a"));
        only.State.Should().Be("ok");
        only.Reason.Should().BeNull();
        only.Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["likeCount"] = 1, ["repostCount"] = 2, ["replyCount"] = 3, ["quoteCount"] = 4, ["bookmarkCount"] = 5,
        });

        // The public AppView needs no session: nothing signs in and nothing is sent to the PDS.
        requests.Should().ContainSingle().Which.Headers.Authorization.Should().BeNull();
        handler.VerifyRequest(r => r.RequestUri!.Host != "public.api.bsky.app", Times.Never());
    }

    [Fact]
    public async Task bluesky_batches_of_more_than_25_are_chunked()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServePosts(handler);
        var ids = Enumerable.Range(1, 60).Select(i => Uri($"r{i}")).ToArray();

        var results = Results(await Sut(handler).ExecuteAsync(Context(ids), default));

        requests.Select(r => UrisOf(r).Count).Should().Equal(25, 25, 10);
        requests.SelectMany(UrisOf).Should().Equal(ids);
        results.Select(r => r.Id).Should().Equal(ids);
        results.Should().OnlyContain(r => r.State == "ok");
    }

    [Fact]
    public async Task a_handle_at_uri_is_resolved_first()
    {
        var handler = new Mock<HttpMessageHandler>();
        var resolves = 0;
        handler.SetupRequest(HttpMethod.Get, ResolveHandle + "?handle=umbhost.net")
               .ReturnsAsync(() =>
               {
                   resolves++;
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"did":"did:plc:abc"}""") };
               });
        var requests = ServePosts(handler);
        var byHandle = Uri("3k2a", "umbhost.net");
        var byHandleToo = Uri("3k2b", "umbhost.net");

        var results = Results(await Sut(handler).ExecuteAsync(Context(byHandle, byHandleToo), default));

        resolves.Should().Be(1);
        UrisOf(requests.Single()).Should().Equal(Uri("3k2a"), Uri("3k2b"));
        results.Select(r => (r.Id, r.State)).Should().Equal((byHandle, "ok"), (byHandleToo, "ok"));
    }

    [Fact]
    public async Task a_handle_that_does_not_resolve_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, ResolveHandle + "?handle=gone.example")
               .ReturnsResponse(HttpStatusCode.BadRequest, new StringContent("""{"error":"HandleNotFound","message":"Unable to resolve handle"}"""));
        ServePosts(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context(Uri("1", "gone.example"), Uri("2")), default));

        results.Select(r => r.State).Should().Equal("gone", "ok");
    }

    [Fact]
    public async Task one_unknown_id_is_gone_and_the_rest_succeed()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServePosts(handler, missing: Uri("deleted"));

        var results = Results(await Sut(handler).ExecuteAsync(Context(Uri("a"), Uri("deleted"), Uri("b")), default));

        results.Select(r => (r.Id, r.State)).Should().Equal((Uri("a"), "ok"), (Uri("deleted"), "gone"), (Uri("b"), "ok"));
        results[1].Values.Should().BeEmpty();
    }

    [Fact]
    public async Task a_malformed_id_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServePosts(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context("https://bsky.app/profile/x/post/1", Uri("a")), default));

        results.Select(r => r.State).Should().Equal("gone", "ok");
        UrisOf(requests.Single()).Should().Equal(Uri("a"));
    }

    [Fact]
    public async Task an_id_with_a_trailing_newline_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServePosts(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context(Uri("a") + "\n", Uri("b")), default));

        results.Select(r => r.State).Should().Equal("gone", "ok");
        UrisOf(requests.Single()).Should().Equal(Uri("b"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.InternalServerError, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, StepRunErrorCategory.Authentication)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, StepRunErrorCategory expected)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(status, new StringContent("""{"error":"x"}"""));

        var result = await Sut(handler).ExecuteAsync(Context(Uri("a")), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public async Task a_timeout_is_retriable()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ThrowsAsync(new TaskCanceledException("timed out"));

        var result = await Sut(handler).ExecuteAsync(Context(Uri("a")), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
    }
}
