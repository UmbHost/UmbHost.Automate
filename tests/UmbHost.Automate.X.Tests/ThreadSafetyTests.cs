using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

// Once part 1 is live, nothing may make Automate retry the step: a retry posts part 1 again. A timeout or an unreadable answer on a
// later part ends the thread with the UMBSOCIAL-THREAD tail instead. Mocked HTTP only.
public class ThreadSafetyTests
{
    private static readonly Guid CredentialsId = Guid.NewGuid();

    private static HttpResponseMessage Ok(int n)
        => new(HttpStatusCode.Created) { Content = new StringContent("{\"data\":{\"id\":\"" + (100 + n) + "\",\"text\":\"x\"}}") };

    private static async Task<ActionResult> Run(Func<int, HttpResponseMessage> answer, Action counted, bool single = false)
    {
        var handler = new Mock<HttpMessageHandler>();
        var n = 0;
        handler.SetupRequest(HttpMethod.Post, "https://api.x.com/2/tweets").Returns((HttpRequestMessage _, CancellationToken _) =>
        {
            counted();
            return Task.FromResult(answer(++n));
        });
        var credentials = new Mock<Umbraco.Automate.OpenIddict.Credentials.IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        var sut = new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object);
        return await sut.ExecuteAsync(ActionTestHarness.Context(
            new CreatePostSettings { Text = "One", ThreadParts = single ? null : """["Two","Three"]""" }, new XConnectionSettings { OAuthCredentialsId = CredentialsId }), default);
    }

    private static string Tail(ActionResult result) => result.Exception!.Message.Split('\n')[^1];

    [Fact]
    public async Task A_timeout_after_part_one_is_live_never_reposts()
    {
        var calls = 0;
        var result = await Run(n => n == 2 ? throw new TaskCanceledException("The request timed out.") : Ok(n), () => calls++);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be("""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://x.com/i/web/status/101"],"failedPartUnknown":true}""");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_bad_publish_response_after_success_is_a_part_failure()
    {
        var calls = 0;
        var result = await Run(n => n == 2 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>gateway</html>") } : Ok(n), () => calls++);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be("""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://x.com/i/web/status/101"],"failedPartUnknown":true}""");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_bad_response_to_a_single_post_is_never_retried_as_if_nothing_went_out()
    {
        var result = await Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }, () => { }, single: true);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().NotBe(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Contain("may have been published");
    }
}
