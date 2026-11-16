using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Tests;

// Once part 1 is live, nothing may make Automate retry the step: a retry posts part 1 again. A timeout or an unreadable answer on a
// later part ends the thread with the UMBSOCIAL-THREAD tail instead. Mocked HTTP only.
public class ThreadSafetyTests
{
    private static HttpResponseMessage Ok(int n)
        => new(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"{{n}}","url":"https://mastodon.social/@u/{{n}}"}""") };

    private static async Task<ActionResult> Run(Func<int, HttpResponseMessage> answer, Action counted, bool single = false)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, "https://mastodon.social/api/v1/statuses").Returns((HttpRequestMessage _, CancellationToken _) =>
        {
            counted();
            return Task.FromResult(answer(++n));
        });
        var sut = new CreateStatusAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options());
        return await sut.ExecuteAsync(ActionTestHarness.Context(
            new CreateStatusSettings { Text = "One", ThreadParts = single ? null : """["Two","Three"]""" },
            new MastodonConnectionSettings { InstanceUrl = "https://mastodon.social", AccessToken = "tok" }), default);
    }

    private static string Tail(ActionResult result) => result.Exception!.Message.Split('\n')[^1];

    [Fact]
    public async Task A_timeout_after_part_one_is_live_never_reposts()
    {
        var calls = 0;
        var result = await Run(n => n == 2 ? throw new TaskCanceledException("The request timed out.") : Ok(n), () => calls++);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be("""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://mastodon.social/@u/1"],"failedPartUnknown":true}""");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_bad_publish_response_after_success_is_a_part_failure()
    {
        var calls = 0;
        var result = await Run(n => n == 2 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") } : Ok(n), () => calls++);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be("""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://mastodon.social/@u/1"],"failedPartUnknown":true}""");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_bad_response_to_a_single_post_is_never_retried_as_if_nothing_went_out()
    {
        var result = await Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }, () => { }, single: true);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().NotBe(StepRunErrorCategory.Validation);
        // Mastodon honours an idempotency key, so a retry gets the same status back rather than a second one.
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
    }
}
