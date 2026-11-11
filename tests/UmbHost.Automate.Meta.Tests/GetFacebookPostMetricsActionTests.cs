using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Reads Facebook Page posts' counts and views. Mocked only: Meta is never called.
/// Counts: <c>shares</c> is <c>{count}</c> (https://developers.facebook.com/docs/graph-api/reference/post/), reactions and comments
/// <c>summary.total_count</c> (https://developers.facebook.com/docs/graph-api/reference/object/reactions/,
/// https://developers.facebook.com/docs/graph-api/reference/object/comments/). Views: <c>post_media_view</c>, the replacement for
/// <c>post_impressions</c> (https://developers.facebook.com/documentation/pages-api/platforminsights/page/deprecated-metrics.md); without
/// <c>read_insights</c> "An empty dataset is returned" (https://developers.facebook.com/documentation/pages-api/platforminsights/page.md).
/// </summary>
public class GetFacebookPostMetricsActionTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";
    private const string Switch = "Umbraco:Automate:Providers:Meta:Insights";
    private const string CountFields = "shares,comments.summary(true).limit(0),reactions.summary(true).limit(0)";

    private static readonly MetaConnectionSettings Conn = new() { PageId = "123", PageAccessToken = "ptok" };

    private static IConfiguration Config(string? insights)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
            .Build();

    private static GetFacebookPostMetricsAction Sut(Mock<HttpMessageHandler> handler, string? insights = "true")
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Config(insights));

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetFacebookPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsCounts(HttpRequestMessage r, string id)
        => r.RequestUri!.GetLeftPart(UriPartial.Path) == Graph + id
           && Uri.UnescapeDataString(r.RequestUri.Query) == "?fields=" + CountFields;

    private static bool IsViews(HttpRequestMessage r, string id)
        => r.RequestUri!.GetLeftPart(UriPartial.Path) == $"{Graph}{id}/insights"
           && Uri.UnescapeDataString(r.RequestUri.Query) == "?metric=post_media_view";

    private static string Counts(string id, int reactions = 28, int comments = 4, int? shares = 3)
    {
        var post = new JsonObject
        {
            ["id"] = id,
            ["comments"] = new JsonObject
            {
                ["data"] = new JsonArray(),
                ["summary"] = new JsonObject { ["order"] = "ranked", ["total_count"] = comments, ["can_comment"] = true },
            },
            ["reactions"] = new JsonObject
            {
                ["data"] = new JsonArray(),
                ["summary"] = new JsonObject { ["total_count"] = reactions, ["viewer_reaction"] = "NONE" },
            },
        };
        if (shares is not null)
        {
            post["shares"] = new JsonObject { ["count"] = shares };
        }

        return post.ToJsonString();
    }

    private static string Views(string id, int views = 900) => new JsonObject
    {
        ["data"] = new JsonArray(new JsonObject
        {
            ["name"] = "post_media_view",
            ["period"] = "lifetime",
            ["values"] = new JsonArray(new JsonObject { ["value"] = views }),
            ["title"] = "Lifetime Post Media Views",
            ["id"] = $"{id}/insights/post_media_view/lifetime",
        }),
        ["paging"] = new JsonObject(),
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
    [InlineData("tru")]
    public async Task insights_off_makes_no_call_and_says_insights_off(string? insights)
    {
        var handler = new Mock<HttpMessageHandler>();

        var results = Results(await Sut(handler, insights).ExecuteAsync(Context("123_456", "123_789"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("insights_off");
        });
        results.Select(r => r.Id).Should().Equal("123_456", "123_789");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[\"123_456\",\"\"]")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new GetFacebookPostMetricsSettings { Ids = ids }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task facebook_counts_and_views_combine()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Counts("123_456")) };
        });
        handler.SetupRequest(r => IsViews(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, Views("123_456"));
        handler.SetupRequest(r => IsCounts(r, "123_789")).ReturnsResponse(HttpStatusCode.OK, Counts("123_789", 1, 0, shares: null));
        handler.SetupRequest(r => IsViews(r, "123_789")).ReturnsResponse(HttpStatusCode.OK, Views("123_789", 7));

        var results = Results(await Sut(handler).ExecuteAsync(Context("123_456", "123_789"), default));

        requests.Single().Headers.Authorization!.Parameter.Should().Be("ptok");
        results.Select(r => (r.Id, r.State)).Should().Equal(("123_456", "ok"), ("123_789", "ok"));
        results[0].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["post_media_view"] = 900, ["reactions"] = 28, ["comments"] = 4, ["shares"] = 3,
        });
        results[1].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["post_media_view"] = 7, ["reactions"] = 1, ["comments"] = 0,
        });
    }

    [Fact]
    public async Task an_empty_insights_dataset_needs_permission()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, Counts("123_456"));
        handler.SetupRequest(r => IsViews(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, """{"data":[],"paging":{}}""");

        var result = Results(await Sut(handler).ExecuteAsync(Context("123_456"), default)).Single();

        result.State.Should().Be("needsPermission");
        result.Reason.Should().Be("scope_missing");
        result.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(200)]
    public async Task a_permissions_error_on_either_call_needs_permission(int code)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsResponse(HttpStatusCode.BadRequest, Error(code));
        handler.SetupRequest(r => IsCounts(r, "123_789")).ReturnsResponse(HttpStatusCode.OK, Counts("123_789"));
        handler.SetupRequest(r => IsViews(r, "123_789")).ReturnsResponse(HttpStatusCode.Forbidden, Error(code));

        var results = Results(await Sut(handler).ExecuteAsync(Context("123_456", "123_789"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("scope_missing");
        });
        handler.VerifyRequest(r => IsViews(r, "123_456"), Times.Never());
    }

    [Fact]
    public async Task a_missing_post_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsResponse(HttpStatusCode.BadRequest, Error(100, 33));

        var result = Results(await Sut(handler).ExecuteAsync(Context("123_456"), default)).Single();

        result.State.Should().Be("gone");
        handler.VerifyRequest(r => IsViews(r, "123_456"), Times.Never());
    }

    [Theory]
    [InlineData("123_456\n")]
    [InlineData("456")]
    [InlineData("123_0456")]
    [InlineData("123_456/insights")]
    public async Task a_malformed_id_is_gone_and_never_sent(string bad)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_789")).ReturnsResponse(HttpStatusCode.OK, Counts("123_789"));
        handler.SetupRequest(r => IsViews(r, "123_789")).ReturnsResponse(HttpStatusCode.OK, Views("123_789"));

        var results = Results(await Sut(handler).ExecuteAsync(Context(bad, "123_789"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal((bad, "gone"), ("123_789", "ok"));
        handler.VerifyAnyRequest(Times.Exactly(2));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 190, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.BadRequest, 17, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.InternalServerError, null, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, 100, StepRunErrorCategory.Validation)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, int? code, StepRunErrorCategory category)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, Counts("123_456"));
        handler.SetupRequest(r => IsViews(r, "123_456")).ReturnsResponse(status, code is null ? "{}" : Error(code.Value));

        var result = await Sut(handler).ExecuteAsync(Context("123_456"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(category);
    }

    [Fact]
    public async Task an_unreadable_answer_is_an_invalid_response()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(r => IsCounts(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, "<html>");

        var result = await Sut(handler).ExecuteAsync(Context("123_456"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }

    [Fact]
    public async Task a_missing_connection_is_authentication_with_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetFacebookPostMetricsSettings { Ids = "[\"123_456\"]" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task the_cancellation_token_reaches_the_call()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        var reached = false;
        handler.SetupRequest(r => IsCounts(r, "123_456")).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            cts.Cancel();
            reached = ct.IsCancellationRequested;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Counts("123_456")) });
        });
        handler.SetupRequest(r => IsViews(r, "123_456")).ReturnsResponse(HttpStatusCode.OK, Views("123_456"));

        await Sut(handler).ExecuteAsync(Context("123_456"), cts.Token);

        reached.Should().BeTrue();
    }
}
