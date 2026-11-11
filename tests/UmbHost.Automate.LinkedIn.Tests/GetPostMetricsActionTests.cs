using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// Reads a batch of a company page's posts' share statistics. Mocked only: LinkedIn is never called.
/// Endpoint, the <c>shares=List(…)</c> and <c>ugcPosts[0]=…</c> forms, the field names, and "not returned … can be assumed to have
/// counts of 0": https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/share-statistics
/// </summary>
public class GetPostMetricsActionTests
{
    private const string Stats = "https://api.linkedin.com/rest/organizationalEntityShareStatistics";
    private const string Switch = "Umbraco:Automate:Providers:LinkedIn:Insights";
    private const string Org = "2414183";

    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static IConfiguration Config(string? insights)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
            .Build();

    private static GetPostMetricsAction Sut(
        Mock<HttpMessageHandler> handler, Mock<IOAuthCredentialsService>? credentials = null, string? token = "tok", string? insights = "true")
    {
        credentials ??= new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object, Config(insights));
    }

    private static ActionContext Context(params string[] ids) => ContextFor(Org, ids);

    private static ActionContext ContextFor(string organizationId, params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { OrganizationId = organizationId, Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsStats(HttpRequestMessage r) => r.RequestUri!.GetLeftPart(UriPartial.Path) == Stats;

    /// <summary>The query, unescaped, as name → value pairs in order.</summary>
    private static List<(string Name, string Value)> Query(HttpRequestMessage r)
        => r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => (Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p[1])))
            .ToList();

    /// <summary>An element shaped as the page's "specific shares" / "specific UGC posts" samples.</summary>
    private static JsonObject Element(string urn, int impressions = 5287) => new()
    {
        ["organizationalEntity"] = "urn:li:organization:" + Org,
        [urn.StartsWith("urn:li:share:", StringComparison.Ordinal) ? "share" : "ugcPost"] = urn,
        ["totalShareStatistics"] = new JsonObject
        {
            ["clickCount"] = 78, ["commentCount"] = 24, ["engagement"] = 0.022886324947985624, ["impressionCount"] = impressions,
            ["likeCount"] = 14, ["shareCount"] = 5, ["uniqueImpressionsCount"] = 4000,
        },
    };

    /// <summary>Answers every statistics call with the elements whose URN the call asked for.</summary>
    private static List<HttpRequestMessage> Serve(Mock<HttpMessageHandler> handler, params JsonObject[] elements)
    {
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(HttpMethod.Get, IsStats).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            var asked = r.RequestUri!.ToString();
            var answer = new JsonArray(elements
                .Where(e => asked.Contains(Uri.EscapeDataString((e["share"] ?? e["ugcPost"])!.GetValue<string>()), StringComparison.Ordinal))
                .Select(e => (JsonNode)e.DeepClone())
                .ToArray());
            var body = new JsonObject { ["elements"] = answer, ["paging"] = new JsonObject { ["count"] = 10, ["start"] = 0, ["links"] = new JsonArray() } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
        });
        return requests;
    }

    private static IReadOnlyList<PostMetricsResult> Results(ActionResult result)
    {
        result.Status.Should().Be(ActionResultStatus.Success, result.Exception?.ToString());
        return result.OutputData.Should().BeOfType<GetPostMetricsOutput>().Subject.Results;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes please")]
    public async Task insights_off_makes_no_call_and_says_insights_off(string? insights)
    {
        var handler = new Mock<HttpMessageHandler>();
        var credentials = new Mock<IOAuthCredentialsService>(MockBehavior.Strict);

        var results = Results(await new GetPostMetricsAction(
                ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object, Config(insights))
            .ExecuteAsync(ContextFor("not-a-number", "urn:li:share:1", "urn:li:ugcPost:2"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("insights_off");
        });
        results.Select(r => r.Id).Should().Equal("urn:li:share:1", "urn:li:ugcPost:2");
        handler.VerifyAnyRequest(Times.Never());
        credentials.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    public async Task any_case_of_true_turns_insights_on(string insights)
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, Element("urn:li:share:1"));

        var results = Results(await Sut(handler, insights: insights).ExecuteAsync(Context("urn:li:share:1"), default));

        results.Single().State.Should().Be("ok");
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[\"urn:li:share:1\",\"\"]")]
    public async Task ids_must_be_a_json_array_of_one_to_one_hundred(string ids)
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetPostMetricsSettings { OrganizationId = Org, Ids = ids }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task the_organization_id_must_be_numeric()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ContextFor("acme", "urn:li:share:1"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task linkedin_reads_shares_and_ugc_posts_in_one_call_each()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = Serve(handler,
            Element("urn:li:share:7132564752928563200"), Element("urn:li:share:7132402731377438720", 5), Element("urn:li:ugcPost:1000000"));

        var results = Results(await Sut(handler).ExecuteAsync(
            Context("urn:li:share:7132564752928563200", "urn:li:ugcPost:1000000", "urn:li:share:7132402731377438720"), default));

        requests.Should().HaveCount(2);
        var shares = Query(requests.Single(r => r.RequestUri!.Query.Contains("shares=", StringComparison.Ordinal)));
        shares.Should().Equal(
            ("q", "organizationalEntity"),
            ("organizationalEntity", "urn:li:organization:" + Org),
            ("shares", "List(urn:li:share:7132564752928563200,urn:li:share:7132402731377438720)"));
        var ugc = Query(requests.Single(r => r.RequestUri!.Query.Contains("ugcPosts", StringComparison.Ordinal)));
        ugc.Should().Equal(
            ("q", "organizationalEntity"),
            ("organizationalEntity", "urn:li:organization:" + Org),
            ("ugcPosts[0]", "urn:li:ugcPost:1000000"));
        requests.Should().AllSatisfy(r =>
        {
            r.Headers.Authorization!.Parameter.Should().Be("tok");
            r.Headers.GetValues("LinkedIn-Version").Should().Equal("202610");
            r.Headers.GetValues("X-Restli-Protocol-Version").Should().Equal("2.0.0");
        });

        results.Select(r => (r.Id, r.State)).Should().Equal(
            ("urn:li:share:7132564752928563200", "ok"), ("urn:li:ugcPost:1000000", "ok"), ("urn:li:share:7132402731377438720", "ok"));
        results[0].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["impressionCount"] = 5287, ["uniqueImpressionsCount"] = 4000, ["clickCount"] = 78, ["likeCount"] = 14,
            ["commentCount"] = 24, ["shareCount"] = 5, ["engagement"] = 0.022886324947985624m,
        });
        results[2].Values["impressionCount"].Should().Be(5);
    }

    [Fact]
    public async Task more_than_twenty_of_one_kind_are_split_into_calls_of_twenty()
    {
        var handler = new Mock<HttpMessageHandler>();
        var urns = Enumerable.Range(1, 45).Select(i => $"urn:li:ugcPost:{i}").ToArray();
        var requests = Serve(handler, urns.Select(u => Element(u)).ToArray());

        var results = Results(await Sut(handler).ExecuteAsync(Context(urns), default));

        requests.Select(r => Query(r).Count(p => p.Name.StartsWith("ugcPosts[", StringComparison.Ordinal))).Should().Equal(20, 20, 5);
        Query(requests[1]).Should().Contain(("ugcPosts[0]", "urn:li:ugcPost:21"));
        results.Should().HaveCount(45).And.AllSatisfy(r => r.State.Should().Be("ok"));
    }

    [Fact]
    public async Task linkedin_omitted_posts_are_zero()
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, Element("urn:li:share:1"));

        var results = Results(await Sut(handler).ExecuteAsync(Context("urn:li:share:1", "urn:li:share:2"), default));

        results[1].State.Should().Be("ok");
        results[1].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["impressionCount"] = 0, ["uniqueImpressionsCount"] = 0, ["clickCount"] = 0, ["likeCount"] = 0,
            ["commentCount"] = 0, ["shareCount"] = 0, ["engagement"] = 0,
        });
    }

    [Theory]
    [InlineData("urn:li:share:123\n")]
    [InlineData("urn:li:share:0123")]
    [InlineData("urn:li:activity:123")]
    [InlineData("123")]
    [InlineData("urn:li:share:12)3")]
    public async Task a_malformed_id_is_gone_and_never_sent(string bad)
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = Serve(handler, Element("urn:li:share:1"));

        var results = Results(await Sut(handler).ExecuteAsync(Context(bad, "urn:li:share:1"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal((bad, "gone"), ("urn:li:share:1", "ok"));
        requests.Should().ContainSingle();
        requests[0].RequestUri!.ToString().Should().NotContain(Uri.EscapeDataString(bad));
    }

    [Fact]
    public async Task only_malformed_ids_make_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();
        var credentials = new Mock<IOAuthCredentialsService>();

        var results = Results(await Sut(handler, credentials).ExecuteAsync(Context("urn:li:share:x"), default));

        results.Single().State.Should().Be("gone");
        handler.VerifyAnyRequest(Times.Never());
        credentials.Verify(c => c.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task a_403_needs_permission()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsStats).ReturnsResponse(HttpStatusCode.Forbidden,
            """{"status":403,"serviceErrorCode":100,"code":"ACCESS_DENIED","message":"Not enough permissions to access: organizationalEntityShareStatistics"}""");

        var results = Results(await Sut(handler).ExecuteAsync(Context("urn:li:share:1", "urn:li:ugcPost:2"), default));

        results.Should().AllSatisfy(r =>
        {
            r.State.Should().Be("needsPermission");
            r.Reason.Should().Be("scope_missing");
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, StepRunErrorCategory.Authentication)]
    [InlineData(HttpStatusCode.BadRequest, StepRunErrorCategory.Validation)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, StepRunErrorCategory category)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsStats).ReturnsResponse(status, "{}");

        var result = await Sut(handler).ExecuteAsync(Context("urn:li:share:1"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(category);
    }

    [Fact]
    public async Task an_unreadable_answer_is_an_invalid_response()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsStats).ReturnsResponse(HttpStatusCode.OK, "<html>");

        var result = await Sut(handler).ExecuteAsync(Context("urn:li:share:1"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }

    [Fact]
    public async Task a_missing_connection_or_token_is_authentication_with_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var noConnection = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetPostMetricsSettings { OrganizationId = Org, Ids = "[\"urn:li:share:1\"]" }), default);
        var noToken = await Sut(handler, token: null).ExecuteAsync(Context("urn:li:share:1"), default);

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
        handler.SetupRequest(HttpMethod.Get, IsStats).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            // HttpClient links the caller's token into the one the handler sees: cancelling the caller's cancels this one.
            cts.Cancel();
            reached = ct.IsCancellationRequested;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"elements":[]}""") });
        });
        var credentials = new Mock<IOAuthCredentialsService>();

        await Sut(handler, credentials).ExecuteAsync(Context("urn:li:share:1"), cts.Token);

        reached.Should().BeTrue();
        credentials.Verify(c => c.GetValidAccessTokenAsync(CredentialsId, cts.Token));
    }

    [Fact]
    public async Task a_cancelled_read_is_cancelled_not_retried()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, IsStats).Returns((HttpRequestMessage _, CancellationToken _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        var result = await Sut(handler).ExecuteAsync(Context("urn:li:share:1"), cts.Token);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Cancelled);
    }
}
