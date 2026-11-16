using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

// Mocked HTTP only: nothing here reaches the real X API.
public class ThreadTests
{
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly XConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object);
    }

    private static Mock<HttpMessageHandler> Serve(List<JsonElement> bodies, int? failOn = null, HttpStatusCode failStatus = HttpStatusCode.BadRequest)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, "https://api.x.com/2/tweets").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            bodies.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone());
            n++;
            return n == failOn
                ? new HttpResponseMessage(failStatus) { Content = new StringContent("""{"title":"Invalid Request"}""") }
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent($$$"""{"data":{"id":"{{{100 + n}}}","text":"x"}}""") };
        });
        return handler;
    }

    [Fact]
    public async Task X_threads_each_part_as_a_reply_to_the_previous()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePostOutput>().Subject;
        output.PostId.Should().Be("101");
        output.ThreadUrls.Should().Equal(
            "https://x.com/i/web/status/101", "https://x.com/i/web/status/102", "https://x.com/i/web/status/103");
        bodies[0].TryGetProperty("reply", out _).Should().BeFalse();
        bodies[1].GetProperty("reply").GetProperty("in_reply_to_tweet_id").GetString().Should().Be("101");
        bodies[2].GetProperty("reply").GetProperty("in_reply_to_tweet_id").GetString().Should().Be("102");
    }

    [Fact]
    public async Task A_failed_part_stops_the_thread_and_reports_it()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies, failOn: 2)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("part 2 of 3").And.Contain("https://x.com/i/web/status/101").And.Contain("could not be posted");
        result.Exception.Message.Split('\n')[^1].Should().Be(
            """UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://x.com/i/web/status/101"],"failedPartUnknown":false}""");
        bodies.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unconfirmed_part_may_be_live_and_is_flagged_unknown()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies, failOn: 2, failStatus: HttpStatusCode.BadGateway)).ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = """["Two"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may also have been published").And.Contain("Check the account before re-posting it");
        result.Exception.Message.Split('\n')[^1].Should().EndWith("\"failedPartUnknown\":true}");
    }

    [Fact]
    public async Task Without_the_new_inputs_nothing_changes()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Serve(bodies)).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Hi" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        bodies.Single().GetRawText().Should().Be("""{"text":"Hi"}""");
    }
}
