using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UmbHost.Automate.Mastodon.Http;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Mastodon.Tests;

/// <summary>
/// Reads statuses' public counts from the posting instance, without a token.
/// Endpoints, auth and responses (200 / 401 authorized-fetch / 404 "does not exist or is private", batch omits missing ids):
/// https://docs.joinmastodon.org/methods/statuses/
/// Batch maximum (DEFAULT_STATUSES_LIMIT = 20, enforced by check_statuses_limit):
/// https://github.com/mastodon/mastodon/blob/main/app/controllers/api/base_controller.rb and
/// https://github.com/mastodon/mastodon/blob/main/app/controllers/api/v1/statuses_controller.rb
/// Status fields (replies_count, reblogs_count, favourites_count, quotes_count): https://docs.joinmastodon.org/entities/Status/
/// Unlisted statuses are readable anonymously (StatusPolicy#show? only restricts private and direct):
/// https://github.com/mastodon/mastodon/blob/main/app/policies/status_policy.rb
/// </summary>
public class GetPostMetricsActionTests
{
    private const string Api = "https://mastodon.social/api/";
    private const string Batch = Api + "v1/statuses";

    private static readonly MastodonConnectionSettings Conn = new() { InstanceUrl = "https://mastodon.social", AccessToken = "write-only-token" };

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory());

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsBatch(HttpRequestMessage r) => r.RequestUri!.GetLeftPart(UriPartial.Path) == Batch;

    private static List<string> IdsOf(HttpRequestMessage request)
        => request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => Uri.UnescapeDataString(p[0]) == "id[]")
            .Select(p => Uri.UnescapeDataString(p[1]))
            .ToList();

    /// <summary>
    /// A real Status (the live public or unlisted post from umbracocommunity.social, Mastodon 4.7.5, Fixtures/Live) under another id,
    /// with counts 1 to 4 so each name is told apart (the live posts' counts were mostly 0). Without quotes it is docs-based: an instance
    /// before 4.5.0 has no <c>quotes_count</c>.
    /// </summary>
    private static JsonObject Status(string id, string visibility = "public", bool withQuotes = true)
    {
        var status = LiveFixture.Object(visibility == "unlisted" ? "status-unlisted" : "status-plain");
        status["id"] = id;
        status["uri"] = $"https://mastodon.social/users/example/statuses/{id}";
        status["url"] = $"https://mastodon.social/@example/{id}";
        status["replies_count"] = 3;
        status["reblogs_count"] = 2;
        status["favourites_count"] = 1;
        status["quotes_count"] = 4;
        if (!withQuotes)
        {
            status.Remove("quotes_count");
        }

        return status;
    }

    /// <summary>Every live status, in the order the account's timeline gave them.</summary>
    private static readonly string[] LiveStatuses =
    [
        "status-plain", "status-unlisted", "status-thread-reply", "status-content-warning-thread-start", "status-link-card", "status-image",
    ];

    [Fact]
    public async Task live_statuses_read_back_their_real_counts()
    {
        var handler = new Mock<HttpMessageHandler>();
        var live = LiveStatuses.Select(LiveFixture.Object).ToList();
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonArray(live.Select(s => (JsonNode)s).ToArray()).ToJsonString()));
        var ids = live.Select(s => s["id"]!.GetValue<string>()).ToArray();

        var results = Results(await Sut(handler).ExecuteAsync(Context(ids), default));

        results.Should().OnlyContain(r => r.State == "ok");
        // Mastodon 4.7.5 sends quotes_count, so all four names come back for every status, the image and link-card ones included.
        results.Should().OnlyContain(r => r.Values.Count == 4);
        // The thread's first post had the one reply; nothing else had been replied to, boosted, favourited or quoted.
        var threadStart = LiveFixture.Object("status-content-warning-thread-start")["id"]!.GetValue<string>();
        results.Single(r => r.Id == threadStart).Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["favourites_count"] = 0, ["reblogs_count"] = 0, ["replies_count"] = 1, ["quotes_count"] = 0,
        });
        results.Where(r => r.Id != threadStart).Should().OnlyContain(r => r.Values.Values.All(v => v == 0));
    }

    /// <summary>Answers the batch route with each id asked for, except those missing (the docs: fewer records than requested).</summary>
    private static List<HttpRequestMessage> ServeBatch(Mock<HttpMessageHandler> handler, params string[] missing)
    {
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
               {
                   requests.Add(r);
                   var statuses = new JsonArray(IdsOf(r).Where(i => !missing.Contains(i)).Select(i => (JsonNode)Status(i)).ToArray());
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(statuses.ToJsonString()) };
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
    [InlineData("[\"1\",\"\"]")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new GetPostMetricsSettings { Ids = ids }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task mastodon_counts_come_back_under_mastodon_names()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServeBatch(handler);

        var only = Results(await Sut(handler).ExecuteAsync(Context("1101"), default)).Should().ContainSingle().Subject;

        only.Id.Should().Be("1101");
        only.State.Should().Be("ok");
        only.Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["favourites_count"] = 1, ["reblogs_count"] = 2, ["replies_count"] = 3, ["quotes_count"] = 4,
        });

        // Our token has only write scopes: the read goes out without it.
        requests.Should().ContainSingle().Which.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task quotes_count_is_left_out_when_the_instance_does_not_send_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonArray(Status("1", withQuotes: false)).ToJsonString()));

        var only = Results(await Sut(handler).ExecuteAsync(Context("1"), default)).Single();

        only.Values.Keys.Should().BeEquivalentTo("favourites_count", "reblogs_count", "replies_count");
    }

    [Fact]
    public async Task mastodon_batches_of_more_than_20_are_chunked()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServeBatch(handler);
        var ids = Enumerable.Range(1, 45).Select(i => i.ToString()).ToArray();

        var results = Results(await Sut(handler).ExecuteAsync(Context(ids), default));

        requests.Select(r => IdsOf(r).Count).Should().Equal(20, 20, 5);
        results.Select(r => r.Id).Should().Equal(ids);
        results.Should().OnlyContain(r => r.State == "ok");
    }

    [Fact]
    public async Task one_unknown_id_is_gone_and_the_rest_succeed()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServeBatch(handler, missing: "2");

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2", "3"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal(("1", "ok"), ("2", "gone"), ("3", "ok"));
    }

    [Fact]
    public async Task a_malformed_id_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServeBatch(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context("../accounts/1", "7"), default));

        results.Select(r => r.State).Should().Equal("gone", "ok");
        IdsOf(requests.Single()).Should().Equal("7");
    }

    [Fact]
    public async Task an_unlisted_status_is_read_without_a_token()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonArray(Status("5", visibility: "unlisted")).ToJsonString()));

        var only = Results(await Sut(handler).ExecuteAsync(Context("5"), default)).Single();

        only.State.Should().Be("ok");
        only.Values["favourites_count"].Should().Be(1);
    }

    [Fact]
    public async Task an_older_instance_falls_back_to_one_status_at_a_time()
    {
        var handler = new Mock<HttpMessageHandler>();
        // Before 4.3.0 the batch route doesn't exist (Rails answers 404).
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsResponse(HttpStatusCode.NotFound, new StringContent("""{"error":"Record not found"}"""));
        handler.SetupRequest(HttpMethod.Get, Api + "v1/statuses/1")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(Status("1", withQuotes: false).ToJsonString()));
        handler.SetupRequest(HttpMethod.Get, Api + "v1/statuses/2")
               .ReturnsResponse(HttpStatusCode.NotFound, new StringContent("""{"error":"Record not found"}"""));
        var ids = new[] { "1", "2" }.Concat(Enumerable.Range(100, 25).Select(i => i.ToString())).ToArray();
        foreach (var id in ids.Skip(2))
        {
            handler.SetupRequest(HttpMethod.Get, Api + $"v1/statuses/{id}")
                   .ReturnsResponse(HttpStatusCode.OK, new StringContent(Status(id).ToJsonString()));
        }

        var results = Results(await Sut(handler).ExecuteAsync(Context(ids), default));

        results[0].State.Should().Be("ok");
        results[0].Values.Should().ContainKey("favourites_count").And.NotContainKey("quotes_count");
        results[1].State.Should().Be("gone");
        results.Skip(2).Should().OnlyContain(r => r.State == "ok");

        // Once the batch route is known missing, the later chunk goes straight to single reads.
        handler.VerifyRequest(HttpMethod.Get, IsBatch, Times.Once());
    }

    [Fact]
    public async Task a_missing_status_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServeBatch(handler, missing: "9");

        var only = Results(await Sut(handler).ExecuteAsync(Context("9"), default)).Single();

        only.State.Should().Be("gone");
        only.Reason.Should().BeNull();
        only.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task an_instance_requiring_auth_is_unavailable_with_scope_missing(HttpStatusCode status)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch)
               .ReturnsResponse(status, new StringContent("""{"error":"This API requires an authenticated user"}"""));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2"), default));

        results.Should().HaveCount(2).And.OnlyContain(r => r.State == "unavailable" && r.Reason == "scope_missing");
    }

    [Fact]
    public async Task a_single_status_requiring_auth_on_an_older_instance_is_unavailable_with_scope_missing()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch).ReturnsResponse(HttpStatusCode.NotFound);
        handler.SetupRequest(HttpMethod.Get, Api + "v1/statuses/1")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent("""{"error":"This API requires an authenticated user"}"""));

        var only = Results(await Sut(handler).ExecuteAsync(Context("1"), default)).Single();

        (only.State, only.Reason).Should().Be(("unavailable", "scope_missing"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.InternalServerError, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, StepRunErrorCategory.ServiceUnavailable)]
    public async Task rate_limited_and_server_errors_are_retriable(HttpStatusCode status, StepRunErrorCategory expected)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(status, new StringContent("""{"error":"x"}"""));

        var result = await Sut(handler).ExecuteAsync(Context("1"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public async Task a_connection_without_an_instance_is_a_configuration_error()
    {
        var handler = new Mock<HttpMessageHandler>();
        var context = ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"1\"]" }, new MastodonConnectionSettings { InstanceUrl = "" });

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task a_redirect_is_unavailable_with_reason_redirected(HttpStatusCode status)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch).ReturnsAsync(() =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://elsewhere.example/api/v1/statuses?id[]=1");
            return response;
        });

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2"), default));

        results.Should().HaveCount(2).And.OnlyContain(r => r.State == "unavailable" && r.Reason == "redirected");
        handler.VerifyAnyRequest(Times.Once());
    }

    [Fact]
    public async Task a_redirect_on_an_older_instance_single_read_is_unavailable_with_reason_redirected()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch).ReturnsResponse(HttpStatusCode.NotFound);
        handler.SetupRequest(HttpMethod.Get, Api + "v1/statuses/1").ReturnsResponse(HttpStatusCode.MovedPermanently);

        var only = Results(await Sut(handler).ExecuteAsync(Context("1"), default)).Single();

        (only.State, only.Reason).Should().Be(("unavailable", "redirected"));
    }

    [Fact]
    public void the_public_client_does_not_follow_redirects()
    {
        var services = new ServiceCollection();
        MastodonClient.AddPublicClient(services);
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(MastodonClient.PublicClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        handler.Should().BeOfType<SocketsHttpHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public async Task a_2xx_body_that_is_not_json_is_an_invalid_response()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch).ReturnsResponse(HttpStatusCode.OK, new StringContent("<html>maintenance</html>"));

        var result = await Sut(handler).ExecuteAsync(Context("1"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }

    [Fact]
    public async Task a_cancelled_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsBatch).Returns(async (HttpRequestMessage _, CancellationToken ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Sut(handler).ExecuteAsync(Context("1"), cts.Token);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Cancelled);
    }

    [Fact]
    public async Task an_http_instance_is_a_configuration_error()
    {
        var handler = new Mock<HttpMessageHandler>();
        var context = ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"1\"]" }, new MastodonConnectionSettings { InstanceUrl = "http://mastodon.social" });

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task an_id_with_a_trailing_newline_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServeBatch(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context("123\n", "5"), default));

        results.Select(r => r.State).Should().Equal("gone", "ok");
        IdsOf(requests.Single()).Should().Equal("5");
    }

    [Fact]
    public async Task a_status_id_with_leading_zeros_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = ServeBatch(handler);

        var results = Results(await Sut(handler).ExecuteAsync(Context("0", "0123", "123"), default));

        results.Select(r => r.State).Should().Equal("gone", "gone", "ok");
        IdsOf(requests.Single()).Should().Equal("123");
    }
}
