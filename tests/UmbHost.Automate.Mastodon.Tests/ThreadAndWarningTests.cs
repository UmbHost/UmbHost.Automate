using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Tests;

public class ThreadAndWarningTests
{
    private const string Statuses = "https://mastodon.social/api/v1/statuses";
    private static readonly MastodonConnectionSettings Conn = new() { InstanceUrl = "https://mastodon.social", AccessToken = "tok" };

    private static CreateStatusAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());

    /// <summary>Answers each status post in turn with ids 1, 2, ... (or a failure for one number) and records the bodies.</summary>
    private static Mock<HttpMessageHandler> Serve(List<JsonElement> bodies, int? failOn = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, Statuses).Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            bodies.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone());
            n++;
            return n == failOn
                ? new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"error":"bad"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"{{n}}","url":"https://mastodon.social/@u/{{n}}"}""") };
        });
        return handler;
    }

    [Fact]
    public async Task Mastodon_sends_the_content_warning()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(
            new CreateStatusSettings { Text = "Hello", ContentWarning = "Spoilers" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        bodies.Single().GetProperty("spoiler_text").GetString().Should().Be("Spoilers");
    }

    [Fact]
    public async Task Mastodon_and_bluesky_thread_each_part_as_a_reply_to_the_previous()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "One",
            Visibility = "unlisted",
            ContentWarning = "cw",
            ThreadParts = """["Two","Three"]""",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateStatusOutput>().Subject;
        output.PostUrl.Should().Be("https://mastodon.social/@u/1");
        output.ThreadUrls.Should().Equal("https://mastodon.social/@u/1", "https://mastodon.social/@u/2", "https://mastodon.social/@u/3");
        bodies.Should().HaveCount(3);
        bodies[0].TryGetProperty("in_reply_to_id", out _).Should().BeFalse();
        bodies[1].GetProperty("in_reply_to_id").GetString().Should().Be("1");
        bodies[2].GetProperty("in_reply_to_id").GetString().Should().Be("2");
        bodies.Select(b => b.GetProperty("visibility").GetString()).Should().AllBe("unlisted");
        bodies.Select(b => b.GetProperty("status").GetString()).Should().Equal("One", "Two", "Three");
        bodies[0].GetProperty("spoiler_text").GetString().Should().Be("cw");
        bodies[1].TryGetProperty("spoiler_text", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_part_stops_the_thread_and_reports_it()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies, failOn: 3)).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "One",
            ThreadParts = """["Two","Three","Four"]""",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("part 3 of 4")
            .And.Contain("https://mastodon.social/@u/1").And.Contain("https://mastodon.social/@u/2");
        bodies.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_thread_with_invalid_parts_posts_nothing()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(
            new CreateStatusSettings { Text = "One", ThreadParts = "not json" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_new_inputs_nothing_changes()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings { Text = "Hi" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        bodies.Single().TryGetProperty("spoiler_text", out _).Should().BeFalse();
        bodies.Single().TryGetProperty("in_reply_to_id", out _).Should().BeFalse();
    }
}
