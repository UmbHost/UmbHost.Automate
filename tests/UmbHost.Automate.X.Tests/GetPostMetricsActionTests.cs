using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Social;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

/// <summary>
/// Reads a batch of the connected account's posts' metrics. Mocked only: X is never called.
/// Endpoint, <c>ids</c> (1 to 100, <c>^[0-9]{1,19}$</c>, comma-separated) and scopes: https://docs.x.com/x-api/posts/get-posts-by-ids
/// Metric field names and the 30-day limit on non-public and organic metrics: https://docs.x.com/x-api/fundamentals/metrics
/// Partial errors (200 with <c>data</c> and <c>errors</c>, the not-found entry): https://docs.x.com/x-api/fundamentals/response-codes-and-errors
/// </summary>
public class GetPostMetricsActionTests
{
    private const string Tweets = "https://api.x.com/2/tweets";
    private const string NotFound = "https://api.x.com/2/problems/resource-not-found";
    private const string NotAuthorised = "https://api.x.com/2/problems/not-authorized-for-resource";

    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly XConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private const string Switch = "Umbraco:Automate:Providers:Twitter:Insights";

    /// <summary>The provider's X metrics switch: on unless a test says otherwise (null leaves it unset).</summary>
    private static IConfiguration Config(string? insights)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
            .Build();

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler, string? token = "tok", string? insights = "true")
        => Sut(handler, new Mock<IOAuthCredentialsService>(), token, insights);

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler, Mock<IOAuthCredentialsService> credentials, string? token = "tok", string? insights = "true")
    {
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object, Config(insights));
    }

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static bool IsTweets(HttpRequestMessage r) => r.RequestUri!.GetLeftPart(UriPartial.Path) == Tweets;

    private static string? Query(HttpRequestMessage r, string name)
        => r.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => Uri.UnescapeDataString(p[0]) == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .SingleOrDefault();

    /// <summary>A Post shaped as https://docs.x.com/x-api/fundamentals/metrics shows it, with or without the owner-only blocks.</summary>
    private static JsonObject Post(string id, bool ownerMetrics = true)
    {
        var post = new JsonObject
        {
            ["id"] = id,
            ["text"] = "Hello",
            ["edit_history_tweet_ids"] = new JsonArray(id),
            ["public_metrics"] = new JsonObject
            {
                ["retweet_count"] = 50, ["reply_count"] = 12, ["like_count"] = 234,
                ["quote_count"] = 5, ["bookmark_count"] = 8, ["impression_count"] = 4500,
            },
        };
        if (ownerMetrics)
        {
            post["non_public_metrics"] = new JsonObject
            {
                ["impression_count"] = 4500, ["url_link_clicks"] = 40, ["user_profile_clicks"] = 9, ["engagements"] = 300,
            };
            post["organic_metrics"] = new JsonObject
            {
                ["impression_count"] = 4500, ["like_count"] = 234, ["reply_count"] = 12, ["retweet_count"] = 50,
                ["url_link_clicks"] = 40, ["user_profile_clicks"] = 9,
            };
        }

        return post;
    }

    private static JsonObject Problem(string id, string type, string title, string detail) => new()
    {
        ["resource_id"] = id, ["resource_type"] = "tweet", ["title"] = title, ["detail"] = detail, ["type"] = type,
    };

    private static List<HttpRequestMessage> Serve(Mock<HttpMessageHandler> handler, JsonArray data, JsonArray? errors = null)
    {
        var requests = new List<HttpRequestMessage>();
        handler.SetupRequest(HttpMethod.Get, IsTweets).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
        {
            requests.Add(r);
            var body = new JsonObject { ["data"] = data.DeepClone() };
            if (errors is not null)
            {
                body["errors"] = errors.DeepClone();
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
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

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task x_returns_public_and_owner_metrics_under_x_names()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = Serve(handler, new JsonArray(Post("1460323737035677698"), Post("1460323737035677699")));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1460323737035677698", "1460323737035677699"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal(("1460323737035677698", "ok"), ("1460323737035677699", "ok"));
        results[0].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["impression_count"] = 4500, ["like_count"] = 234, ["retweet_count"] = 50, ["reply_count"] = 12,
            ["quote_count"] = 5, ["bookmark_count"] = 8, ["url_link_clicks"] = 40, ["user_profile_clicks"] = 9,
        });

        var request = requests.Should().ContainSingle().Subject;
        Query(request, "ids").Should().Be("1460323737035677698,1460323737035677699");
        Query(request, "tweet.fields").Should().Be("public_metrics,non_public_metrics,organic_metrics");
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("tok");
    }

    [Fact]
    public async Task x_after_30_days_returns_public_metrics_only()
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, new JsonArray(Post("1", ownerMetrics: false)));

        var only = Results(await Sut(handler).ExecuteAsync(Context("1"), default)).Single();

        only.State.Should().Be("ok");
        only.Values.Keys.Should().BeEquivalentTo(
            "impression_count", "like_count", "retweet_count", "reply_count", "quote_count", "bookmark_count");
    }

    [Fact]
    public async Task x_not_found_errors_are_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, new JsonArray(Post("1")),
            new JsonArray(Problem("456", NotFound, "Not Found Error", "Could not find tweet with id: [456].")));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "456"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal(("1", "ok"), ("456", "gone"));
        results[1].Values.Should().BeEmpty();
        results[1].Reason.Should().BeNull();
    }

    [Fact]
    public async Task all_ids_not_found_are_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        // With nothing found there is no data block, only errors.
        handler.SetupRequest(HttpMethod.Get, IsTweets).ReturnsResponse(HttpStatusCode.OK, new StringContent(new JsonObject
        {
            ["errors"] = new JsonArray(Problem("7", NotFound, "Not Found Error", "Could not find tweet with id: [7].")),
        }.ToJsonString()));

        var only = Results(await Sut(handler).ExecuteAsync(Context("7"), default)).Single();

        only.State.Should().Be("gone");
    }

    [Fact]
    public async Task an_id_x_withholds_for_another_reason_is_unavailable()
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, new JsonArray(Post("1")),
            new JsonArray(Problem("2", NotAuthorised, "Authorization Error", "Sorry, you are not authorized to see the Post with id: [2].")));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2", "3"), default));

        results.Select(r => (r.Id, r.State, r.Reason)).Should().Equal(
            ("1", "ok", null), ("2", "unavailable", "scope_missing"), ("3", "gone", null));
    }

    [Fact]
    public async Task a_malformed_x_id_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = Serve(handler, new JsonArray(Post("123")));

        var results = Results(await Sut(handler).ExecuteAsync(Context("abc", "0123", "12345678901234567890", "123"), default));

        results.Select(r => r.State).Should().Equal("gone", "gone", "gone", "ok");
        Query(requests.Single(), "ids").Should().Be("123");
    }

    [Fact]
    public async Task only_malformed_ids_make_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var results = Results(await Sut(handler).ExecuteAsync(Context("abc"), default));

        results.Single().State.Should().Be("gone");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task a_full_batch_of_100_is_one_call()
    {
        var handler = new Mock<HttpMessageHandler>();
        var ids = Enumerable.Range(1, 100).Select(i => i.ToString()).ToArray();
        var requests = Serve(handler, new JsonArray(ids.Select(i => (JsonNode)Post(i)).ToArray()));

        var results = Results(await Sut(handler).ExecuteAsync(Context(ids), default));

        requests.Should().ContainSingle();
        Query(requests[0], "ids")!.Split(',').Should().HaveCount(100);
        results.Should().HaveCount(100).And.OnlyContain(r => r.State == "ok");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, StepRunErrorCategory.RateLimiting)]
    [InlineData(HttpStatusCode.InternalServerError, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, StepRunErrorCategory.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, StepRunErrorCategory.Authentication)]
    public async Task rate_limited_and_server_errors_are_retriable_and_401_is_not(HttpStatusCode status, StepRunErrorCategory expected)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(status, new StringContent("""{"title":"x","detail":"x","type":"about:blank","status":0}"""));

        var result = await Sut(handler).ExecuteAsync(Context("1"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public async Task a_missing_connection_is_authentication_and_makes_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"1\"]" }, new XConnectionSettings()), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task an_expired_token_is_authentication_and_makes_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler, token: null).ExecuteAsync(Context("1"), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task with_the_provider_switch_off_x_is_never_called(string? insights)
    {
        // UmbSocial only runs this action with its own X switch on, so being called here stands for "Social's switch is on".
        var handler = new Mock<HttpMessageHandler>();
        var credentials = new Mock<IOAuthCredentialsService>();

        var results = Results(await Sut(handler, credentials, insights: insights).ExecuteAsync(Context("1", "2"), default));

        results.Select(r => (r.Id, r.State, r.Reason)).Should().Equal(
            ("1", "needsPermission", "insights_off"), ("2", "needsPermission", "insights_off"));
        handler.VerifyAnyRequest(Times.Never());
        // Not even a token refresh, which would also go to X.
        credentials.Verify(c => c.GetValidAccessTokenAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task with_the_provider_switch_on_x_is_read()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = Serve(handler, new JsonArray(Post("1")));

        var only = Results(await Sut(handler, insights: "true").ExecuteAsync(Context("1"), default)).Single();

        only.State.Should().Be("ok");
        requests.Should().ContainSingle();
    }
}
