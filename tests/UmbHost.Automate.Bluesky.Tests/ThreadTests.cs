using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Bluesky.Actions;
using UmbHost.Automate.Bluesky.Connection;

namespace UmbHost.Automate.Bluesky.Tests;

public class ThreadTests
{
    private const string Pds = "https://bsky.social/xrpc/";
    private static readonly BlueskyConnectionSettings Conn = new() { ServiceUrl = "https://bsky.social", Identifier = "umbhost.net", AppPassword = "p" };

    private static Mock<HttpMessageHandler> Serve(List<JsonElement> records, int? failOn = null, HttpStatusCode failStatus = HttpStatusCode.BadRequest, List<string>? requests = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.server.createSession")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                   """{"accessJwt":"jwt","refreshJwt":"r","handle":"umbhost.net","did":"did:plc:abc"}"""));
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, Pds + "com.atproto.repo.createRecord").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            var raw = await r.Content!.ReadAsStringAsync();
            requests?.Add(raw);
            records.Add(JsonDocument.Parse(raw).RootElement.GetProperty("record").Clone());
            n++;
            return n == failOn
                ? new HttpResponseMessage(failStatus) { Content = new StringContent("""{"error":"InvalidRequest"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"uri":"at://did:plc:abc/app.bsky.feed.post/k{{n}}","cid":"cid{{n}}"}""") };
        });
        return handler;
    }

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());

    [Fact]
    public async Task Mastodon_and_bluesky_thread_each_part_as_a_reply_to_the_previous()
    {
        var records = new List<JsonElement>();

        var result = await Sut(Serve(records)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePostOutput>().Subject;
        output.PostUrl.Should().Be("https://bsky.app/profile/umbhost.net/post/k1");
        output.ThreadUrls.Should().Equal(
            "https://bsky.app/profile/umbhost.net/post/k1", "https://bsky.app/profile/umbhost.net/post/k2", "https://bsky.app/profile/umbhost.net/post/k3");
        records.Should().HaveCount(3);
        records[0].TryGetProperty("reply", out _).Should().BeFalse();
        var second = records[1].GetProperty("reply");
        second.GetProperty("root").GetProperty("uri").GetString().Should().EndWith("/k1");
        second.GetProperty("root").GetProperty("cid").GetString().Should().Be("cid1");
        second.GetProperty("parent").GetProperty("uri").GetString().Should().EndWith("/k1");
        var third = records[2].GetProperty("reply");
        third.GetProperty("root").GetProperty("cid").GetString().Should().Be("cid1");
        third.GetProperty("parent").GetProperty("uri").GetString().Should().EndWith("/k2");
        third.GetProperty("parent").GetProperty("cid").GetString().Should().Be("cid2");
    }

    [Fact]
    public async Task A_failed_part_stops_the_thread_and_reports_it()
    {
        var records = new List<JsonElement>();

        var result = await Sut(Serve(records, failOn: 2)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("part 2 of 3").And.Contain("https://bsky.app/profile/umbhost.net/post/k1").And.Contain("could not be posted");
        result.Exception.Message.Split('\n')[^1].Should().Be(
            """UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://bsky.app/profile/umbhost.net/post/k1"],"failedPartUnknown":false}""");
        records.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unconfirmed_part_may_be_live_and_is_flagged_unknown()
    {
        var records = new List<JsonElement>();

        var result = await Sut(Serve(records, failOn: 2, failStatus: HttpStatusCode.BadGateway)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may also have been published").And.Contain("Check the account before re-posting it");
        result.Exception.Message.Split('\n')[^1].Should().EndWith("\"failedPartUnknown\":true}");
    }

    [Fact]
    public async Task A_part_over_the_limit_is_refused_before_anything_is_posted()
    {
        var records = new List<JsonElement>();
        var tooLong = new string('a', 301);

        var result = await Sut(Serve(records)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = JsonSerializer.Serialize(new[] { tooLong }) }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        records.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_new_inputs_nothing_changes()
    {
        var records = new List<JsonElement>();
        var requests = new List<string>();

        var result = await Sut(Serve(records, requests: requests)).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        System.Text.RegularExpressions.Regex.Replace(requests.Single(), "\"createdAt\":\"[^\"]+\"", "\"createdAt\":\"T\"").Should().Be(
            """{"repo":"did:plc:abc","collection":"app.bsky.feed.post","record":{"$type":"app.bsky.feed.post","text":"Hi","createdAt":"T"}}""");
    }
}
