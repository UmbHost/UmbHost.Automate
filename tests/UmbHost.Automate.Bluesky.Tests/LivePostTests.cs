using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The connector's records against the posts it made live on 2026-11-17, as the public AppView serves them back
/// (<c>app.bsky.feed.getAuthorFeed</c>, Fixtures/Live/author-feed.json): the record it sends must be the record Bluesky stored, and the
/// answer it reads must give the post's real link.
/// </summary>
/// <remarks>
/// Sign-in (<c>createSession</c>) and <c>createRecord</c> answers weren't captured; their envelopes are docs-based
/// (https://docs.bsky.app/docs/api/com-atproto-repo-create-record), carrying the live posts' uri and cid.
/// </remarks>
public class LivePostTests
{
    private const string Pds = "https://bsky.social/xrpc/";
    private const string Did = "did:plc:aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Handle = "example.bsky.social";

    private static readonly BlueskyConnectionSettings Conn = new() { ServiceUrl = "https://bsky.social", Identifier = Handle, AppPassword = "app-pass" };

    /// <summary>The live feed item whose post's rkey (the last part of its at-uri) is given.</summary>
    private static JsonObject Item(string rkey)
        => LiveFixture.Object("author-feed")["feed"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => i["post"]!["uri"]!.GetValue<string>().EndsWith("/" + rkey, StringComparison.Ordinal));

    private static JsonObject Record(string rkey) => Item(rkey)["post"]!["record"]!.AsObject();

    private static string Created(string rkey)
    {
        var post = Item(rkey)["post"]!;
        return new JsonObject { ["uri"] = post["uri"]!.GetValue<string>(), ["cid"] = post["cid"]!.GetValue<string>() }.ToJsonString();
    }

    /// <summary>A PDS that signs in as the live account and answers each createRecord with the next live post, recording what was sent.</summary>
    private static (Mock<HttpMessageHandler> Handler, List<JsonObject> Sent) LivePds(params string[] answers)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent($$"""{"accessJwt":"jwt","refreshJwt":"r","handle":"{{Handle}}","did":"{{Did}}"}"""));
        var sent = new List<JsonObject>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   sent.Add(JsonNode.Parse(await r.Content!.ReadAsStringAsync())!.AsObject());
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Created(answers[sent.Count - 1])) };
               });
        return (handler, sent);
    }

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());

    private static void ShouldMatchLive(JsonNode? sent, JsonNode? live)
        => JsonNode.DeepEquals(sent, live).Should().BeTrue($"we sent {sent?.ToJsonString()} and Bluesky stored {live?.ToJsonString()}");

    [Fact]
    public async Task A_link_card_post_is_the_record_bluesky_stored_live_facet_offsets_included()
    {
        // The live text has an em dash (three UTF-8 bytes) before the link, so the facet's byte offsets differ from its char offsets.
        var live = Record("3n2jfbtpoy22p");
        var external = live["embed"]!["external"]!;
        var (handler, sent) = LivePds("3n2jfbtpoy22p");

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = live["text"]!.GetValue<string>(),
            LinkUrl = external["uri"]!.GetValue<string>(),
            LinkTitle = external["title"]!.GetValue<string>(),
            LinkDescription = external["description"]!.GetValue<string>(),
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var record = sent.Single()["record"]!;
        ShouldMatchLive(record["facets"], live["facets"]);
        ShouldMatchLive(record["embed"], live["embed"]);
        record["text"]!.GetValue<string>().Should().Be(live["text"]!.GetValue<string>());
        result.OutputData.Should().BeOfType<CreatePostOutput>().Which.PostUrl.Should().Be($"https://bsky.app/profile/{Handle}/post/3n2jfbtpoy22p");
    }

    [Fact]
    public async Task An_image_post_is_the_record_bluesky_stored_live()
    {
        // Posted before images carried an aspect ratio (a39ec8c): with no dimension reader the record still matches that live one.
        var live = Record("3n2jf65jndg2z");
        var image = live["embed"]!["images"]![0]!;
        var (handler, sent) = LivePds("3n2jf65jndg2z");
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/banner.jpg")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), configure: r =>
                   r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"));
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.uploadBlob")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonObject { ["blob"] = image["image"]!.DeepClone() }.ToJsonString()));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = live["text"]!.GetValue<string>(),
            ImageUrl = "https://cdn.example.com/banner.jpg",
            ImageAltText = image["alt"]!.GetValue<string>(),
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        ShouldMatchLive(sent.Single()["record"]!["embed"], live["embed"]);
    }

    [Fact]
    public async Task A_thread_reply_points_at_part_one_as_the_live_reply_did()
    {
        var (handler, sent) = LivePds("3n2jfifbun22p", "3n2jfifhlku24");

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings
        {
            Text = Record("3n2jfifbun22p")["text"]!.GetValue<string>(),
            ThreadParts = new JsonArray(Record("3n2jfifhlku24")["text"]!.GetValue<string>()).ToJsonString(),
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        ShouldMatchLive(sent[1]["record"]!["reply"], Record("3n2jfifhlku24")["reply"]);
        result.OutputData.Should().BeOfType<CreatePostOutput>().Which.ThreadUrls.Should().Equal(
            $"https://bsky.app/profile/{Handle}/post/3n2jfifbun22p",
            $"https://bsky.app/profile/{Handle}/post/3n2jfifhlku24");
    }

    [Fact]
    public async Task A_refused_sign_in_reads_as_blueskys_own_words()
    {
        // The answer Bluesky gave the live run on 2026-11-17 when the app password was wrong (from the site log).
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent(LiveFixture.Text("error-401-sign-in")));
        var context = ActionTestHarness.Context(new CreatePostSettings { Text = "hi" }, Conn);

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Be("Invalid identifier or password");
        context.LogEntries.Should().ContainSingle().Which.Message.Should().Be(
            "Bluesky sign-in returned 401: " + LiveFixture.Text("error-401-sign-in"));
    }
}
