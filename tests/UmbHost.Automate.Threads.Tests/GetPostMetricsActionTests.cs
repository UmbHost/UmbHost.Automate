using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Social;
using UmbHost.Automate.Threads.Actions;
using UmbHost.Automate.Threads.Connection;

namespace UmbHost.Automate.Threads.Tests;

/// <summary>
/// Reads Threads posts' insights, one call per post. Mocked only: Threads is never called.
/// Endpoint, metric names, the answer's shape and the empty array for <c>REPOST_FACADE</c> posts:
/// https://developers.facebook.com/docs/threads/insights. Graph error codes:
/// https://developers.facebook.com/docs/graph-api/guides/error-handling
/// </summary>
public class GetPostMetricsActionTests
{
    private const string Graph = "https://graph.threads.com/v1.0/";
    private const string Switch = "Umbraco:Automate:Providers:Threads:Insights";
    private const string Metrics = "views,likes,replies,reposts,quotes,shares";

    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly ThreadsConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static IConfiguration Config(string? insights)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
            .Build();

    private static Mock<IOAuthCredentialsService> Credentials(string? token = "THLong")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetCredentialsAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(() => token is null ? null : new OAuthCredentials
        {
            Id = CredentialsId,
            Provider = "Threads",
            AccessToken = token,
            DateModified = DateTime.UtcNow - TimeSpan.FromHours(2),
        });
        return credentials;
    }

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler, Mock<IOAuthCredentialsService>? credentials = null, string? insights = "true")
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(),
            new ThreadsTokens(handler.CreateClientFactory(), (credentials ?? Credentials()).Object), Config(insights));

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsInsights(HttpRequestMessage r, string id)
        => r.Method == HttpMethod.Get && r.RequestUri!.GetLeftPart(UriPartial.Path) == $"{Graph}{id}/insights";

    private static bool IsAnyInsights(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/insights", StringComparison.Ordinal);

    /// <summary>An insights answer shaped as the page's sample: <c>data[] { name, period, values[{value}], title, description, id }</c>.</summary>
    private static string Insights(string id, params (string Name, int Value)[] metrics)
        => new JsonObject
        {
            ["data"] = new JsonArray(metrics.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name,
                ["period"] = "lifetime",
                ["values"] = new JsonArray(new JsonObject { ["value"] = m.Value }),
                ["title"] = m.Name,
                ["description"] = "…",
                ["id"] = $"{id}/insights/{m.Name}/lifetime",
            }).ToArray()),
        }.ToJsonString();

    private static string Error(int code, int? subcode = null)
    {
        var error = new JsonObject { ["message"] = "…", ["type"] = "OAuthException", ["code"] = code, ["fbtrace_id"] = "A1" };
        if (subcode is not null)
        {
            error["error_subcode"] = subcode;
        }

        return new JsonObject { ["error"] = error }.ToJsonString();
    }

    private static IReadOnlyList<PostMetricsResult> Results(ActionResult result)
    {
        result.Status.Should().Be(ActionResultStatus.Success, result.Exception?.ToString());
        return result.OutputData.Should().BeOfType<GetPostMetricsOutput>().Subject.Results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("on")]
    public async Task insights_off_makes_no_call_and_says_insights_off(string? insights)
    {
        var handler = new Mock<HttpMessageHandler>();
        var credentials = new Mock<IOAuthCredentialsService>(MockBehavior.Strict);

        var results = Results(await Sut(handler, credentials, insights).ExecuteAsync(Context("17890000000000001", "17890000000000002"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("insights_off");
        });
        results.Select(r => r.Id).Should().Equal("17890000000000001", "17890000000000002");
        handler.VerifyAnyRequest(Times.Never());
        credentials.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[\"1\",\"\"]")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new GetPostMetricsSettings { Ids = ids }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task threads_reads_root_post_insights()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(r => IsInsights(r, "17890000000000001")).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Insights("17890000000000001",
                    ("views", 1200), ("likes", 100), ("replies", 7), ("reposts", 3), ("quotes", 2), ("shares", 5))),
            };
        });
        handler.SetupRequest(r => IsInsights(r, "17890000000000002"))
            .ReturnsResponse(HttpStatusCode.OK, Insights("17890000000000002", ("likes", 4), ("replies", 0)));

        var results = Results(await Sut(handler).ExecuteAsync(Context("17890000000000001", "17890000000000002"), default));

        var query = HttpUtility.ParseQueryString(requests.Single().RequestUri!.Query);
        query["metric"].Should().Be(Metrics);
        query["access_token"].Should().Be("THLong");
        results.Select(r => (r.Id, r.State)).Should().Equal(("17890000000000001", "ok"), ("17890000000000002", "ok"));
        results[0].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["views"] = 1200, ["likes"] = 100, ["replies"] = 7, ["reposts"] = 3, ["quotes"] = 2, ["shares"] = 5,
        });
        results[1].Values.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["likes"] = 4, ["replies"] = 0 });
    }

    [Fact]
    public async Task a_repost_facade_is_unsupported()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(IsAnyInsights).ReturnsResponse(HttpStatusCode.OK, """{"data":[]}""");

        var result = Results(await Sut(handler).ExecuteAsync(Context("17890000000000001"), default)).Single();

        result.State.Should().Be("unavailable");
        result.Reason.Should().Be("unsupported");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(299)]
    public async Task a_permissions_error_needs_permission(int code)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17890000000000001")).ReturnsResponse(HttpStatusCode.BadRequest, Error(code));
        handler.SetupRequest(r => IsInsights(r, "17890000000000002")).ReturnsResponse(HttpStatusCode.OK, Insights("17890000000000002", ("likes", 1)));

        var results = Results(await Sut(handler).ExecuteAsync(Context("17890000000000001", "17890000000000002"), default));

        results[0].State.Should().Be("needsPermission");
        results[0].Reason.Should().Be("scope_missing");
        results[1].State.Should().Be("ok");
    }

    [Fact]
    public async Task a_missing_post_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17890000000000001")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100, 33));
        handler.SetupRequest(r => IsInsights(r, "17890000000000002")).ReturnsResponse(HttpStatusCode.NotFound, "{}");

        var results = Results(await Sut(handler).ExecuteAsync(Context("17890000000000001", "17890000000000002"), default));

        results.Select(r => r.State).Should().Equal("gone", "gone");
    }

    [Theory]
    [InlineData("17890000000000001\n")]
    [InlineData("0178")]
    [InlineData("abc")]
    [InlineData("1/insights")]
    public async Task a_malformed_id_is_gone_and_never_sent(string bad)
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(IsAnyInsights).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Insights("1", ("likes", 1))) };
        });

        var results = Results(await Sut(handler).ExecuteAsync(Context(bad, "17890000000000001"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal((bad, "gone"), ("17890000000000001", "ok"));
        requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath.Should().Be("/v1.0/17890000000000001/insights");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 190, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.Unauthorized, null, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.BadRequest, 4, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.TooManyRequests, null, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, StepRunErrorCategory.ServiceUnavailable)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, int? code, StepRunErrorCategory category)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(IsAnyInsights).ReturnsResponse(status, code is null ? "{}" : Error(code.Value));

        var result = await Sut(handler).ExecuteAsync(Context("17890000000000001"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(category);
    }

    [Fact]
    public async Task an_unreadable_answer_is_an_invalid_response()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(IsAnyInsights).ReturnsResponse(HttpStatusCode.OK, "<html>");

        var result = await Sut(handler).ExecuteAsync(Context("17890000000000001"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }

    [Fact]
    public async Task a_missing_connection_or_token_is_authentication_with_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var noConnection = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"17890000000000001\"]" }), default);
        var noToken = await Sut(handler, Credentials(token: null)).ExecuteAsync(Context("17890000000000001"), default);

        noConnection.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        noToken.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task the_cancellation_token_reaches_the_call()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        var reached = false;
        handler.SetupRequest(IsAnyInsights).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            cts.Cancel();
            reached = ct.IsCancellationRequested;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Insights("1", ("likes", 1))) });
        });
        var credentials = Credentials();

        await Sut(handler, credentials).ExecuteAsync(Context("17890000000000001"), cts.Token);

        reached.Should().BeTrue();
        credentials.Verify(c => c.GetCredentialsAsync(CredentialsId, cts.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 100)]
    [InlineData(HttpStatusCode.Conflict, null)]
    public async Task one_rejected_post_in_three_settles_alone(HttpStatusCode status, int? code)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "1")).ReturnsResponse(HttpStatusCode.OK, Insights("1", ("likes", 1)));
        handler.SetupRequest(r => IsInsights(r, "2")).ReturnsResponse(status, code is null ? "{}" : Error(code.Value));
        handler.SetupRequest(r => IsInsights(r, "3")).ReturnsResponse(HttpStatusCode.OK, Insights("3", ("likes", 3)));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2", "3"), default));

        results.Select(r => (r.Id, r.State, r.Reason)).Should().Equal(("1", "ok", null), ("2", "unavailable", "rejected"), ("3", "ok", null));
    }

    [Fact]
    public async Task a_padded_true_turns_insights_on()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(IsAnyInsights).ReturnsResponse(HttpStatusCode.OK, Insights("1", ("likes", 1)));

        var result = Results(await Sut(handler, insights: " true ").ExecuteAsync(Context("1"), default)).Single();

        result.State.Should().Be("ok");
    }
}
