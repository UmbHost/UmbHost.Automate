using System.Net;
using System.Web;
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
// - Parameters: https://developers.facebook.com/docs/threads/reference/publishing (every call takes `access_token` (required, string) as a
//   parameter; alt_text up to 1,000 characters; reply_to_id; GET /{container-id}?fields=status,error_message)
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

    private static Dictionary<string, string> Form(string body)
    {
        var form = HttpUtility.ParseQueryString(body);
        return form.AllKeys.ToDictionary(k => k!, k => form[k]!);
    }

    private static bool Get(HttpRequestMessage r, string path, string fields)
    {
        if (r.Method != HttpMethod.Get || r.RequestUri!.AbsolutePath != path)
        {
            return false;
        }

        var query = HttpUtility.ParseQueryString(r.RequestUri.Query);
        return query["fields"] == fields && query["access_token"] is not null;
    }

    /// <summary>
    /// A Threads API that answers each call in turn: containers (c1, c2…), their status, publishing (m1, m2…) and permalinks. Each
    /// container's form and each request's token are recorded; <see cref="Publish"/> and <see cref="Container"/> can change an answer.
    /// </summary>
    private sealed class FakeThreads
    {
        public Mock<HttpMessageHandler> Handler { get; } = new();
        public List<Dictionary<string, string>> Containers { get; } = [];
        public List<string?> Tokens { get; } = [];
        public int Published { get; private set; }

        /// <summary>Answers the n-th publish request (1-based); null for the normal answer.</summary>
        public Func<int, string, HttpResponseMessage?> Publish { get; set; } = (_, _) => null;

        /// <summary>Answers the n-th container request (1-based); null for the normal answer.</summary>
        public Func<int, HttpResponseMessage?> Container { get; set; } = _ => null;

        public Func<string, HttpResponseMessage?> Permalink { get; set; } = _ => null;

        public string Status { get; set; } = "FINISHED";

        public FakeThreads()
        {
            Handler.SetupRequest(r => Get(r, "/v1.0/me", "id,username")).Returns((HttpRequestMessage r, CancellationToken _) =>
            {
                Tokens.Add(HttpUtility.ParseQueryString(r.RequestUri!.Query)["access_token"]);
                return Task.FromResult(Json("""{"id":"1789","username":"umbhost"}"""));
            });
            Handler.SetupRequest(HttpMethod.Post, Graph + "v1.0/1789/threads").Returns(async (HttpRequestMessage r, CancellationToken _) =>
            {
                var form = Form(await r.Content!.ReadAsStringAsync());
                Tokens.Add(form.GetValueOrDefault("access_token"));
                form.Remove("access_token");
                Containers.Add(form);
                return Container(Containers.Count) ?? Json($$"""{"id":"c{{Containers.Count}}"}""");
            });
            Handler.SetupRequest(r => r.RequestUri!.AbsolutePath.StartsWith("/v1.0/c") && Get(r, r.RequestUri.AbsolutePath, "status,error_message"))
                .Returns(() => Task.FromResult(Json($$"""{"status":"{{Status}}","error_message":"{{(Status == "ERROR" ? "FAILED_DOWNLOADING_IMAGE" : "")}}"}""")));
            Handler.SetupRequest(HttpMethod.Post, Graph + "v1.0/1789/threads_publish").Returns(async (HttpRequestMessage r, CancellationToken _) =>
            {
                var form = Form(await r.Content!.ReadAsStringAsync());
                Tokens.Add(form.GetValueOrDefault("access_token"));
                Published++;
                return Publish(Published, form["creation_id"]) ?? Json($$"""{"id":"m{{form["creation_id"][1..]}}"}""");
            });
            Handler.SetupRequest(r => r.RequestUri!.AbsolutePath.StartsWith("/v1.0/m") && Get(r, r.RequestUri.AbsolutePath, "permalink"))
                .Returns((HttpRequestMessage r, CancellationToken _) =>
                {
                    var id = r.RequestUri!.AbsolutePath[6..];
                    return Task.FromResult(Permalink(id) ?? Json($$"""{"permalink":"https://www.threads.com/@umbhost/post/{{id}}"}"""));
                });
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    private static string Tail(ActionResult result) => result.Exception!.Message.Split('\n')[^1];

    private const string Url1 = "https://www.threads.com/@umbhost/post/m1";

    [Fact]
    public async Task Threads_posts_text_and_an_image()
    {
        var threads = new FakeThreads();

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings
        {
            Text = "Autumn menu https://example.com/menu",
            ImageUrl = "https://example.com/media/menu.jpg",
            ImageAltText = "A bowl of soup",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateThreadsPostOutput>().Subject;
        output.PostId.Should().Be("m1");
        output.PostUrl.Should().Be(Url1);
        output.ThreadUrls.Should().Equal(Url1);
        threads.Containers.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["media_type"] = "IMAGE",
            ["text"] = "Autumn menu https://example.com/menu",
            ["image_url"] = "https://example.com/media/menu.jpg",
            ["alt_text"] = "A bowl of soup",
        });
    }

    [Fact]
    public async Task Every_call_carries_access_token_as_a_parameter_as_the_threads_docs_show()
    {
        var threads = new FakeThreads();

        await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        threads.Tokens.Should().HaveCount(3).And.AllBe("THLong");
        threads.Handler.VerifyRequest(r => r.Headers.Authorization != null, Times.Never());
    }

    [Fact]
    public async Task Threads_threads_each_part_as_a_reply()
    {
        var threads = new FakeThreads();

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateThreadsPostOutput>().Which.ThreadUrls.Should().Equal(
            Url1, "https://www.threads.com/@umbhost/post/m2", "https://www.threads.com/@umbhost/post/m3");
        threads.Containers.Select(c => c["media_type"]).Should().AllBe("TEXT");
        threads.Containers[0].Should().NotContainKey("reply_to_id");
        threads.Containers[1]["reply_to_id"].Should().Be("m1");
        threads.Containers[2]["reply_to_id"].Should().Be("m2");
    }

    [Fact]
    public async Task No_thread_is_a_single_post()
    {
        var threads = new FakeThreads();

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = "[]" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        threads.Containers.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_part_stops_the_thread_and_reports_it()
    {
        var threads = new FakeThreads { Publish = (n, _) => n == 2 ? Json("""{"error":{"message":"Invalid parameter"}}""", HttpStatusCode.BadRequest) : null };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be($$"""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["{{Url1}}"],"failedPartUnknown":false}""");
        threads.Containers.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_timeout_after_part_one_is_live_never_reposts()
    {
        var threads = new FakeThreads { Publish = (n, _) => n == 2 ? throw new TaskCanceledException("The request timed out.") : null };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be($$"""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["{{Url1}}"],"failedPartUnknown":true}""");
        threads.Published.Should().Be(2);
    }

    [Fact]
    public async Task A_timeout_preparing_a_later_part_ends_the_thread_and_that_part_is_not_live()
    {
        var threads = new FakeThreads { Container = n => n == 2 ? throw new TaskCanceledException("The request timed out.") : null };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be($$"""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["{{Url1}}"],"failedPartUnknown":false}""");
        threads.Published.Should().Be(1);
    }

    [Fact]
    public async Task A_bad_publish_response_after_success_is_a_part_failure()
    {
        var threads = new FakeThreads { Publish = (n, _) => n == 2 ? Json("<html>gateway</html>") : null };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ThreadParts = """["Two","Three"]""" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        Tail(result).Should().Be($$"""UMBSOCIAL-THREAD {"failedPart":2,"total":3,"posted":["{{Url1}}"],"failedPartUnknown":true}""");
        threads.Containers.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_bad_publish_response_to_a_single_post_is_not_retried()
    {
        var threads = new FakeThreads { Publish = (_, _) => Json("not json") };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public async Task A_timeout_publishing_the_first_part_is_an_unknown_outcome()
    {
        var threads = new FakeThreads { Publish = (_, _) => throw new TaskCanceledException("The request timed out.") };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public async Task A_timeout_before_anything_is_live_is_retried_and_never_thrown()
    {
        var threads = new FakeThreads { Container = _ => throw new TaskCanceledException("The request timed out.") };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
        threads.Published.Should().Be(0);
    }

    [Fact]
    public async Task A_permalink_that_times_out_leaves_the_post_published_without_its_link()
    {
        var threads = new FakeThreads { Permalink = _ => throw new TaskCanceledException("The request timed out.") };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateThreadsPostOutput>().Which.PostId.Should().Be("m1");
    }

    [Fact]
    public async Task An_unconfirmed_first_post_may_exist_and_is_not_retried()
    {
        var threads = new FakeThreads { Publish = (_, _) => Json("{}", HttpStatusCode.InternalServerError) };

        var result = await Sut(threads.Handler).ExecuteAsync(
            ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    [Fact]
    public async Task A_container_threads_could_not_build_is_a_definite_failure_and_nothing_is_published()
    {
        var threads = new FakeThreads { Status = "ERROR" };

        var result = await Sut(threads.Handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateThreadsPostSettings { Text = "One", ImageUrl = "https://example.com/missing.jpg" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Contain("FAILED_DOWNLOADING_IMAGE");
        threads.Published.Should().Be(0);
    }

    [Fact]
    public async Task A_week_old_token_is_refreshed_and_saved_before_posting()
    {
        var threads = new FakeThreads();
        threads.Handler.SetupRequest(HttpMethod.Get, Graph + "refresh_access_token?grant_type=th_refresh_token&access_token=THLong")
            .ReturnsResponse(HttpStatusCode.OK, """{"access_token":"THFresh","token_type":"bearer","expires_in":5183944}""");
        var credentials = Credentials(TimeSpan.FromDays(8));
        OAuthCredentials? saved = null;
        credentials.Setup(c => c.UpdateCredentialsAsync(It.IsAny<OAuthCredentials>(), It.IsAny<CancellationToken>()))
            .Callback((OAuthCredentials c, CancellationToken _) => saved = c).Returns(Task.CompletedTask);

        var result = await Sut(threads.Handler, credentials).ExecuteAsync(ActionTestHarness.Context(new CreateThreadsPostSettings { Text = "One" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        saved!.AccessToken.Should().Be("THFresh");
        threads.Tokens.Should().AllBe("THFresh");
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

    [Fact]
    public async Task Validating_the_connection_sends_access_token_as_a_parameter()
    {
        var threads = new FakeThreads();
        var connection = new ThreadsConnectionType(
            new Umbraco.Automate.Core.Connections.ConnectionTypeInfrastructure(Mock.Of<Umbraco.Automate.Core.Settings.IEditableModelResolver>()),
            Mock.Of<IOAuthCredentialsService>(),
            new ThreadsTokens(threads.Handler.CreateClientFactory(), Credentials(TimeSpan.FromHours(2)).Object),
            threads.Handler.CreateClientFactory());

        var result = await connection.ValidateAsync(Conn, default);

        result.Status.Should().Be(Umbraco.Automate.Core.Connections.ConnectionValidationStatus.Success);
        result.Message.Should().Contain("@umbhost");
        threads.Tokens.Should().Equal("THLong");
        threads.Handler.VerifyRequest(r => r.Headers.Authorization != null, Times.Never());
    }
}
