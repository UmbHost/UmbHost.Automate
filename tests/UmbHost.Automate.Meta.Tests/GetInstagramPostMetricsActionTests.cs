using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// Reads Instagram media insights. Mocked only: Meta is never called.
/// Metrics, the answer's shape and "Insights data is not available for any media within an Instagram Media album":
/// https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights. <c>media_type</c> values:
/// https://developers.facebook.com/docs/instagram-platform/reference/instagram-media. What the insights call answers for an album
/// is not documented (UNVERIFIED); the fixture uses a plain Graph error.
/// </summary>
public class GetInstagramPostMetricsActionTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";
    private const string Switch = "Umbraco:Automate:Providers:Meta:Insights";
    private const string Metrics = "views,reach,likes,comments,shares,saved,reposts";

    private static readonly MetaConnectionSettings Conn = new() { PageId = "123", PageAccessToken = "ptok", InstagramAccountId = "1784" };

    private static IConfiguration Config(string? insights)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
            .Build();

    private static GetInstagramPostMetricsAction Sut(Mock<HttpMessageHandler> handler, string? insights = "true")
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Config(insights));

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetInstagramPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsInsights(HttpRequestMessage r, string id)
        => r.RequestUri!.GetLeftPart(UriPartial.Path) == $"{Graph}{id}/insights";

    private static bool IsMediaType(HttpRequestMessage r, string id)
        => r.RequestUri!.GetLeftPart(UriPartial.Path) == Graph + id && HttpUtility.ParseQueryString(r.RequestUri.Query)["fields"] == "media_type";

    /// <summary>An insights answer shaped as the page's sample.</summary>
    private static string Insights(string id, params (string Name, int Value)[] metrics) => new JsonObject
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
        return result.OutputData.Should().BeOfType<MetaPostMetricsOutput>().Subject.Results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("1")]
    public async Task insights_off_makes_no_call_and_says_insights_off(string? insights)
    {
        var handler = new Mock<HttpMessageHandler>();

        var results = Results(await Sut(handler, insights).ExecuteAsync(Context("17900000000000001", "17900000000000002"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("insights_off");
        });
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[\"1\",\"\"]")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new GetInstagramPostMetricsSettings { Ids = ids }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task instagram_reads_lifetime_insights()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Insights("17900000000000001",
                    ("views", 5000), ("reach", 3100), ("likes", 210), ("comments", 12), ("shares", 9), ("saved", 30), ("reposts", 2))),
            };
        });

        var result = Results(await Sut(handler).ExecuteAsync(Context("17900000000000001"), default)).Single();

        var query = HttpUtility.ParseQueryString(requests.Single().RequestUri!.Query);
        query["metric"].Should().Be(Metrics);
        query["period"].Should().Be("lifetime");
        requests.Single().Headers.Authorization!.Parameter.Should().Be("ptok");
        result.State.Should().Be("ok");
        result.Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["views"] = 5000, ["reach"] = 3100, ["likes"] = 210, ["comments"] = 12, ["shares"] = 9, ["saved"] = 30, ["reposts"] = 2,
        });
    }

    [Fact]
    public async Task instagram_albums_are_unsupported()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100));
        handler.SetupRequest(r => IsMediaType(r, "17900000000000001"))
            .ReturnsResponse(HttpStatusCode.OK, """{"media_type":"CAROUSEL_ALBUM","id":"17900000000000001"}""");

        var result = Results(await Sut(handler).ExecuteAsync(Context("17900000000000001"), default)).Single();

        result.State.Should().Be("unavailable");
        result.Reason.Should().Be("unsupported");
    }

    [Fact]
    public async Task one_rejected_post_in_three_settles_alone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "1")).ReturnsResponse(HttpStatusCode.OK, Insights("1", ("likes", 1)));
        handler.SetupRequest(r => IsInsights(r, "2")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100));
        handler.SetupRequest(r => IsMediaType(r, "2")).ReturnsResponse(HttpStatusCode.OK, """{"media_type":"IMAGE","id":"2"}""");
        handler.SetupRequest(r => IsInsights(r, "3")).ReturnsResponse(HttpStatusCode.OK, Insights("3", ("likes", 3)));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2", "3"), default));

        results.Select(r => (r.Id, r.State, r.Reason)).Should().Equal(("1", "ok", null), ("2", "unavailable", "rejected"), ("3", "ok", null));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task a_post_whose_type_cant_be_looked_up_is_rejected(HttpStatusCode status)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "2")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100));
        handler.SetupRequest(r => IsMediaType(r, "2")).ReturnsResponse(status, Error(100));

        var result = Results(await Sut(handler).ExecuteAsync(Context("2"), default)).Single();

        result.State.Should().Be("unavailable");
        result.Reason.Should().Be("rejected");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 4, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.TooManyRequests, null, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.BadRequest, 190, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, StepRunErrorCategory.ServiceUnavailable)]
    public async Task a_throttled_or_unauthorised_album_lookup_fails_the_step_as_such(HttpStatusCode status, int? code, StepRunErrorCategory category)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "2")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100));
        handler.SetupRequest(r => IsMediaType(r, "2")).ReturnsResponse(status, code is null ? "{}" : Error(code.Value));

        var result = await Sut(handler).ExecuteAsync(Context("2"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(category);
    }

    [Fact]
    public async Task the_cancellation_token_reaches_the_album_lookup()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        var reached = false;
        handler.SetupRequest(r => IsInsights(r, "2")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100));
        handler.SetupRequest(r => IsMediaType(r, "2")).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            cts.Cancel();
            reached = ct.IsCancellationRequested;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"media_type":"IMAGE"}""") });
        });

        await Sut(handler).ExecuteAsync(Context("2"), cts.Token);

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task a_padded_true_turns_insights_on()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "1")).ReturnsResponse(HttpStatusCode.OK, Insights("1", ("likes", 1)));

        var result = Results(await Sut(handler, " true ").ExecuteAsync(Context("1"), default)).Single();

        result.State.Should().Be("ok");
    }

    [Fact]
    public async Task an_empty_insights_dataset_needs_permission()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.OK, """{"data":[]}""");

        var result = Results(await Sut(handler).ExecuteAsync(Context("17900000000000001"), default)).Single();

        result.State.Should().Be("needsPermission");
        result.Reason.Should().Be("scope_missing");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(299)]
    public async Task a_permissions_error_needs_permission(int code)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.BadRequest, Error(code));
        handler.SetupRequest(r => IsInsights(r, "17900000000000002")).ReturnsResponse(HttpStatusCode.OK, Insights("17900000000000002", ("likes", 1)));

        var results = Results(await Sut(handler).ExecuteAsync(Context("17900000000000001", "17900000000000002"), default));

        results[0].State.Should().Be("needsPermission");
        results[0].Reason.Should().Be("scope_missing");
        results[1].State.Should().Be("ok");
        handler.VerifyRequest(r => IsMediaType(r, "17900000000000001"), Times.Never());
    }

    [Fact]
    public async Task a_missing_post_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100, 33));

        var result = Results(await Sut(handler).ExecuteAsync(Context("17900000000000001"), default)).Single();

        result.State.Should().Be("gone");
    }

    [Theory]
    [InlineData("17900000000000001\n")]
    [InlineData("0179")]
    [InlineData("1_2")]
    public async Task a_malformed_id_is_gone_and_never_sent(string bad)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.OK, Insights("17900000000000001", ("likes", 1)));

        var results = Results(await Sut(handler).ExecuteAsync(Context(bad, "17900000000000001"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal((bad, "gone"), ("17900000000000001", "ok"));
        handler.VerifyAnyRequest(Times.Once());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 190, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.BadRequest, 4, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, StepRunErrorCategory.ServiceUnavailable)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, int? code, StepRunErrorCategory category)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(status, code is null ? "{}" : Error(code.Value));

        var result = await Sut(handler).ExecuteAsync(Context("17900000000000001"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(category);
        handler.VerifyRequest(r => IsMediaType(r, "17900000000000001"), Times.Never());
    }

    [Fact]
    public async Task an_unreadable_answer_is_an_invalid_response()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).ReturnsResponse(HttpStatusCode.OK, "<html>");

        var result = await Sut(handler).ExecuteAsync(Context("17900000000000001"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }

    [Fact]
    public async Task a_missing_connection_is_authentication_with_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetInstagramPostMetricsSettings { Ids = "[\"17900000000000001\"]" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task the_cancellation_token_reaches_the_call()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        var reached = false;
        handler.SetupRequest(r => IsInsights(r, "17900000000000001")).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            cts.Cancel();
            reached = ct.IsCancellationRequested;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Insights("1", ("likes", 1))) });
        });

        await Sut(handler).ExecuteAsync(Context("17900000000000001"), cts.Token);

        reached.Should().BeTrue();
    }
}
