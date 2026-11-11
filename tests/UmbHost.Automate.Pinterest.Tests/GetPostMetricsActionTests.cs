using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Actions;
using UmbHost.Automate.Pinterest.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Pinterest.Tests;

/// <summary>
/// Reads each Pin's lifetime metrics, one <c>GET /v5/pins/{pin_id}?pin_metrics=true</c> per id. Mocked only.
/// Source for the operation (<c>pins/get</c>, scopes <c>boards:read</c> + <c>pins:read</c>, rate category <c>org_read</c>, responses
/// 200/401/403/404/429), <c>query_pin_metrics</c>, the <c>Pin.pin_metrics</c> example and <c>Pinterest.Lib.Error</c>:
/// https://raw.githubusercontent.com/pinterest/api-description/main/v5/openapi.yaml
/// </summary>
public class GetPostMetricsActionTests
{
    private const string Pins = "https://api.pinterest.com/v5/pins/";

    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly PinterestConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static GetPostMetricsAction Sut(Mock<HttpMessageHandler> handler, string? token = "tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), credentials.Object);
    }

    private static ActionContext Context(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static string PinUrl(string id) => $"{Pins}{id}?pin_metrics=true";

    /// <summary>A Pin with <c>pin_metrics</c> as the spec's example shows it (<c>Pin.pin_metrics</c>), plus <c>save</c> when asked.</summary>
    private static JsonObject Pin(string id, bool withSave = false, bool lifetime = true)
    {
        var lifetimeMetrics = new JsonObject
        {
            ["pin_click"] = 7, ["impression"] = 2, ["clickthrough"] = 3, ["reaction"] = 10, ["comment"] = 2,
        };
        if (withSave)
        {
            lifetimeMetrics["save"] = 4;
        }

        var metrics = new JsonObject
        {
            ["90d"] = new JsonObject { ["pin_click"] = 1, ["impression"] = 1, ["clickthrough"] = 1 },
        };
        if (lifetime)
        {
            metrics["lifetime_metrics"] = lifetimeMetrics;
        }

        return new JsonObject
        {
            ["id"] = id,
            ["created_at"] = "2026-11-01T09:00:00",
            ["board_id"] = "549755885175",
            ["title"] = "Hello",
            ["pin_metrics"] = metrics,
        };
    }

    private static StringContent Error(int code, string message)
        => new(new JsonObject { ["code"] = code, ["message"] = message }.ToJsonString());

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
    public async Task pinterest_reads_lifetime_metrics_per_pin()
    {
        var handler = new Mock<HttpMessageHandler>();
        var requests = new List<HttpRequestMessage>();
        foreach (var (id, save) in new[] { ("101", true), ("102", false) })
        {
            var pin = Pin(id, withSave: save);
            // A metric outside the known list is never passed on.
            pin["pin_metrics"]!["lifetime_metrics"]!["video_start"] = 99;
            handler.SetupRequest(HttpMethod.Get, PinUrl(id)).ReturnsAsync((HttpRequestMessage r, CancellationToken _) =>
            {
                requests.Add(r);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pin.ToJsonString()) };
            });
        }

        var results = Results(await Sut(handler).ExecuteAsync(Context("101", "102"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal(("101", "ok"), ("102", "ok"));
        results[0].Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["impression"] = 2, ["save"] = 4, ["pin_click"] = 7, ["clickthrough"] = 3, ["reaction"] = 10, ["comment"] = 2,
        });
        results[1].Values.Keys.Should().BeEquivalentTo("impression", "pin_click", "clickthrough", "reaction", "comment");

        requests.Should().HaveCount(2).And.OnlyContain(r => r.Headers.Authorization!.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "tok");
    }

    [Fact]
    public async Task pinterest_404_is_gone()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, PinUrl("1")).ReturnsResponse(HttpStatusCode.NotFound, Error(50, "Pin not found."));
        handler.SetupRequest(HttpMethod.Get, PinUrl("2")).ReturnsResponse(HttpStatusCode.OK, new StringContent(Pin("2").ToJsonString()));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2"), default));

        results.Select(r => (r.Id, r.State)).Should().Equal(("1", "gone"), ("2", "ok"));
        results[0].Values.Should().BeEmpty();
    }

    [Fact]
    public async Task a_pin_without_lifetime_metrics_is_unsupported()
    {
        var handler = new Mock<HttpMessageHandler>();
        // Before 2023-03-20 only video and idea Pins have lifetime metrics; the 90d block is not lifetime and is not used.
        handler.SetupRequest(HttpMethod.Get, PinUrl("1")).ReturnsResponse(HttpStatusCode.OK, new StringContent(Pin("1", lifetime: false).ToJsonString()));
        var noMetrics = Pin("2");
        noMetrics["pin_metrics"] = null;
        handler.SetupRequest(HttpMethod.Get, PinUrl("2")).ReturnsResponse(HttpStatusCode.OK, new StringContent(noMetrics.ToJsonString()));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2"), default));

        results.Should().OnlyContain(r => r.State == "unavailable" && r.Reason == "unsupported" && r.Values.Count == 0);
    }

    [Fact]
    public async Task a_forbidden_pin_needs_permission_and_the_rest_succeed()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, PinUrl("1")).ReturnsResponse(HttpStatusCode.Forbidden, Error(29, "You are not permitted to access that resource."));
        handler.SetupRequest(HttpMethod.Get, PinUrl("2")).ReturnsResponse(HttpStatusCode.OK, new StringContent(Pin("2").ToJsonString()));

        var results = Results(await Sut(handler).ExecuteAsync(Context("1", "2"), default));

        results.Select(r => (r.Id, r.State, r.Reason)).Should().Equal(("1", "needsPermission", "scope_missing"), ("2", "ok", null));
    }

    [Fact]
    public async Task a_malformed_pin_id_is_gone_and_never_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, PinUrl("5")).ReturnsResponse(HttpStatusCode.OK, new StringContent(Pin("5").ToJsonString()));

        var results = Results(await Sut(handler).ExecuteAsync(Context("../boards", "05", "5"), default));

        results.Select(r => r.State).Should().Equal("gone", "gone", "ok");
        handler.VerifyAnyRequest(Times.Once());
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
        handler.SetupAnyRequest().ReturnsResponse(status, Error(1, "x"));

        var result = await Sut(handler).ExecuteAsync(Context("1"), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(expected);
    }

    [Fact]
    public async Task a_missing_connection_is_authentication_and_makes_no_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"1\"]" }, new PinterestConnectionSettings()), default);

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
}
