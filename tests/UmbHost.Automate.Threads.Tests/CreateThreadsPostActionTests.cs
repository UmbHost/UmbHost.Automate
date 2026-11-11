using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Threads.Actions;
using UmbHost.Automate.Threads.Connection;

namespace UmbHost.Automate.Threads.Tests;

// Mocked HTTP only: nothing here reaches the real Threads API. Requests and responses follow Meta's published Threads API documentation:
// - Posts: https://developers.facebook.com/docs/threads/posts (a media container, POST /{user-id}/threads with media_type TEXT or IMAGE,
//   text, image_url; then POST /{user-id}/threads_publish with creation_id)
// - Parameters: https://developers.facebook.com/docs/threads/reference/publishing (alt_text up to 1,000 characters; reply_to_id;
//   GET /{container-id}?fields=status,error_message)
// - Replies: https://developers.facebook.com/docs/threads/retrieve-and-manage-replies/create-replies
// - Long-lived tokens: https://developers.facebook.com/docs/threads/get-started/long-lived-tokens
public class CreateThreadsPostActionTests
{
    private const string Graph = "https://graph.threads.com/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly ThreadsConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static Mock<IOAuthCredentialsService> Credentials(TimeSpan age, string token = "THLong")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetCredentialsAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(() => new OAuthCredentials
        {
            Id = CredentialsId,
            Provider = "Threads",
            AccessToken = token,
            DateModified = DateTime.UtcNow - age,
        });
        return credentials;
    }

    private static CreateThreadsPostAction Sut(Mock<HttpMessageHandler> handler, Mock<IOAuthCredentialsService>? credentials = null)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), new ThreadsTokens(handler.CreateClientFactory(), (credentials ?? Credentials(TimeSpan.FromHours(2))).Object))
        {
            StatusPollDelay = TimeSpan.Zero,
        };

    /// <summary>
    /// Serves the account, containers (c1, c2…), their status, publishing (m1, m2…) and permalinks, recording each container's form.
    /// </summary>
    private static Mock<HttpMessageHandler> Serve(List<Dictionary<string, string>> containers, int? failPublishOn = null, HttpStatusCode failStatus = HttpStatusCode.BadRequest, string status = "FINISHED")
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Graph + "v1.0/me?fields=id,username")
            .ReturnsResponse(HttpStatusCode.OK, """{"id":"1789","username":"umbhost"}""");
        handler.SetupRequest(HttpMethod.Post, Graph + "v1.0/1789/threads").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            containers.Add(System.Web.HttpUtility.ParseQueryString(await r.Content!.ReadAsStringAsync()) is var form
                ? form.AllKeys.ToDictionary(k => k!, k => form[k]!)
                : []);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"c{{containers.Count}}"}""") };
        });
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.AbsolutePath.StartsWith("/v1.0/c") && r.RequestUri.Query == "?fields=status,error_message")
            .ReturnsResponse(HttpStatusCode.OK, $$"""{"status":"{{status}}","error_message":"{{(status == "ERROR" ? "FAILED_DOWNLOADING_IMAGE" : "")}}"}""");
        var published = 0;
        handler.SetupRequest(HttpMethod.Post, Graph + "v1.0/1789/threads_publish").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            var creation = System.Web.HttpUtility.ParseQueryString(await r.Content!.ReadAsStringAsync())["creation_id"];
            published++;
            return published == failPublishOn
                ? new HttpResponseMessage(failStatus) { Content = new StringContent("""{"error":{"message":"Invalid parameter"}}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"id":"m{{creation![1..]}}"}""") };
        });
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.AbsolutePath.StartsWith("/v1.0/m") && r.RequestUri.Query == "?fields=permalink")
            .Returns((HttpRequestMessage r, CancellationToken _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"permalink":"https://www.threads.com/@umbhost/post/{{r.RequestUri!.AbsolutePath[6..]}}"}"""),
            }));
        return handler;
    }

    [Fact]
    public async Task Threads_posts_text_and_an_image()
    {
        var containers = new List<Dictionary<string, string>>();
        var handler = Serve(containers);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings
        {
            Text = "Autumn menu https://example.com/menu",
            ImageUrl = "https://example.com/media/menu.jpg",
            ImageAltText = "A bowl of soup",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateThreadsPostOutput>().Subject;
        output.PostId.Should().Be("m1");
        output.PostUrl.Should().Be("https://www.threads.com/@umbhost/post/m1");
        output.ThreadUrls.Should().Equal("https://www.threads.com/@umbhost/post/m1");
        containers.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["media_type"] = "IMAGE",
            ["text"] = "Autumn menu https://example.com/menu",
            ["image_url"] = "https://example.com/media/menu.jpg",
            ["alt_text"] = "A bowl of soup",
        });
        handler.VerifyRequest(r => r.Headers.Authorization!.ToString() == "Bearer THLong", Times.AtLeastOnce());
    }

    [Fact]
    public async Task Threads_threads_each_part_as_a_reply()
    {
        var containers = new List<Dictionary<string, string>>();

        var result = await Sut(Serve(containers)).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateThreadsPostOutput>().Which.ThreadUrls.Should().Equal(
            "https://www.threads.com/@umbhost/post/m1", "https://www.threads.com/@umbhost/post/m2", "https://www.threads.com/@umbhost/post/m3");
        containers.Select(c => c["media_type"]).Should().AllBe("TEXT");
        containers[0].Should().NotContainKey("reply_to_id");
        containers[1]["reply_to_id"].Should().Be("m1");
        containers[2]["reply_to_id"].Should().Be("m2");
    }

    [Fact]
    public async Task No_thread_is_a_single_post()
    {
        var containers = new List<Dictionary<string, string>>();

        var result = await Sut(Serve(containers)).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = "[]" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        containers.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_part_stops_the_thread_and_reports_it()
    {
        var containers = new List<Dictionary<string, string>>();

        var result = await Sut(Serve(containers, failPublishOn: 2)).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Split('\n')[^1].Should().Be(
            """UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["https://www.threads.com/@umbhost/post/m1"],"failedPartUnknown":false}""");
        containers.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unconfirmed_first_post_may_exist_and_is_not_retried()
    {
        var containers = new List<Dictionary<string, string>>();

        var result = await Sut(Serve(containers, failPublishOn: 1, failStatus: HttpStatusCode.InternalServerError)).ExecuteAsync(
            ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public async Task A_container_threads_could_not_build_is_a_definite_failure_and_nothing_is_published()
    {
        var containers = new List<Dictionary<string, string>>();
        var handler = Serve(containers, status: "ERROR");

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ImageUrl = "https://example.com/missing.jpg" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Contain("FAILED_DOWNLOADING_IMAGE");
        handler.VerifyRequest(HttpMethod.Post, Graph + "v1.0/1789/threads_publish", Times.Never());
    }

    [Fact]
    public async Task A_week_old_token_is_refreshed_and_saved_before_posting()
    {
        var containers = new List<Dictionary<string, string>>();
        var handler = Serve(containers);
        handler.SetupRequest(HttpMethod.Get, Graph + "refresh_access_token?grant_type=th_refresh_token&access_token=THLong")
            .ReturnsResponse(HttpStatusCode.OK, """{"access_token":"THFresh","token_type":"bearer","expires_in":5183944}""");
        var credentials = Credentials(TimeSpan.FromDays(8));
        OAuthCredentials? saved = null;
        credentials.Setup(c => c.UpdateCredentialsAsync(It.IsAny<OAuthCredentials>(), It.IsAny<CancellationToken>()))
            .Callback((OAuthCredentials c, CancellationToken _) => saved = c).Returns(Task.CompletedTask);

        var result = await Sut(handler, credentials).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        saved!.AccessToken.Should().Be("THFresh");
        handler.VerifyRequest(r => r.RequestUri!.AbsolutePath == "/v1.0/1789/threads" && r.Headers.Authorization!.Parameter == "THFresh", Times.Once());
    }

    [Fact]
    public async Task A_lapsed_token_asks_for_the_connection_to_be_authorised_again()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler, Credentials(TimeSpan.FromDays(61))).ExecuteAsync(
            ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Contain("Re-authorise");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task Alt_text_over_1000_characters_is_refused()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings
        {
            Text = "One", ImageUrl = "https://example.com/a.jpg", ImageAltText = new string('a', 1001),
        }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }
}
