using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Actions;
using UmbHost.Automate.Pinterest.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Pinterest.Tests;

/// <summary>
/// The connector against the Pinterest sandbox's real answers, captured on 2026-11-18 after the live test made its Pins there
/// (Fixtures/Live, ids and the account redacted).
/// </summary>
public class LiveSandboxTests
{
    private const string Api = "https://api-sandbox.pinterest.com/v5/";
    private const string Token = "pina_sandbox_token";

    private static readonly PinterestConnectionSettings Conn = new()
    {
        Environment = PinterestEnvironment.Sandbox,
        SignIn = PinterestSignIn.AccessToken,
        AccessToken = Token,
    };

    private static readonly IOAuthCredentialsService NoOAuth = Mock.Of<IOAuthCredentialsService>(MockBehavior.Strict);

    /// <summary>The live Pin with a title, description, link and alt text (the first Pin of the live set).</summary>
    private static JsonObject LivePin()
        => LiveFixture.Object("board-pins")["items"]!.AsArray().Select(p => p!.AsObject()).Single(p => p["link"] is not null);

    private static IReadOnlyList<PostMetricsResult> Results(ActionResult result)
    {
        result.Status.Should().Be(ActionResultStatus.Success);
        return result.OutputData.Should().BeOfType<GetPostMetricsOutput>().Subject.Results;
    }

    private static ActionContext MetricsContext(params string[] ids)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = JsonSerializer.Serialize(ids) }, Conn);

    private static GetPostMetricsAction Metrics(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), NoOAuth);

    [Fact]
    public async Task The_live_pins_metrics_come_back_under_the_names_pinterest_sent()
    {
        var pin = LiveFixture.Text("pin-with-metrics");
        var id = LiveFixture.Object("pin-with-metrics")["id"]!.GetValue<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, $"{Api}pins/{id}?pin_metrics=true").ReturnsResponse(HttpStatusCode.OK, new StringContent(pin));

        var only = Results(await Metrics(handler).ExecuteAsync(MetricsContext(id), default)).Single();

        only.State.Should().Be("ok");
        // The live lifetime block sends outbound_click (not the spec example's clickthrough) and save; profile_visit and user_follow
        // came as null and last_updated as a date, so none of those is passed on.
        only.Values.Should().BeEquivalentTo(new Dictionary<string, decimal>
        {
            ["impression"] = 0, ["save"] = 0, ["pin_click"] = 0, ["outbound_click"] = 0, ["reaction"] = 0, ["comment"] = 0,
        });
    }

    [Fact]
    public async Task Outbound_clicks_in_the_live_shape_are_passed_on()
    {
        var pin = LiveFixture.Object("pin-with-metrics");
        var lifetime = pin["pin_metrics"]!["lifetime_metrics"]!;
        lifetime["impression"] = 40;
        lifetime["outbound_click"] = 3;
        lifetime["save"] = 2;
        var id = pin["id"]!.GetValue<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, $"{Api}pins/{id}?pin_metrics=true").ReturnsResponse(HttpStatusCode.OK, new StringContent(pin.ToJsonString()));

        var only = Results(await Metrics(handler).ExecuteAsync(MetricsContext(id), default)).Single();

        only.Values["outbound_click"].Should().Be(3);
        only.Values["impression"].Should().Be(40);
        only.Values["save"].Should().Be(2);
    }

    [Fact]
    public async Task A_listed_pin_without_pin_metrics_is_unsupported()
    {
        // GET /v5/boards/{id}/pins sent each Pin with "pin_metrics": null; a Pin answered that way has no lifetime metrics to read.
        var pin = LivePin();
        pin["pin_metrics"].Should().BeNull();
        var id = pin["id"]!.GetValue<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, $"{Api}pins/{id}?pin_metrics=true").ReturnsResponse(HttpStatusCode.OK, new StringContent(pin.ToJsonString()));

        var only = Results(await Metrics(handler).ExecuteAsync(MetricsContext(id), default)).Single();

        only.State.Should().Be("unavailable");
        only.Reason.Should().Be(PostMetricsReason.Unsupported);
    }

    private static CreatePinSettings PinSettings(JsonObject live) => new()
    {
        BoardId = live["board_id"]!.GetValue<string>(),
        Title = live["title"]!.GetValue<string>(),
        Description = live["description"]!.GetValue<string>(),
        Link = live["link"]!.GetValue<string>(),
        ImageUrl = "https://example.com/media/banner.jpg",
        ImageAltText = live["alt_text"]!.GetValue<string>(),
    };

    private static async Task<ActionResult> CreatePin(HttpStatusCode status, string answer, List<JsonObject>? sent = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "pins").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            sent?.Add(JsonNode.Parse(await r.Content!.ReadAsStringAsync())!.AsObject());
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        });

        return await new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), NoOAuth)
            .ExecuteAsync(ActionTestHarness.Context(PinSettings(LivePin()), Conn), default);
    }

    [Fact]
    public async Task A_created_pin_answered_with_the_live_pin_shape_gives_its_id_and_link()
    {
        // POST /v5/pins answers with the Pin entity; this is the live Pin as the board listing served it.
        var live = LivePin();
        var sent = new List<JsonObject>();

        var result = await CreatePin(HttpStatusCode.Created, live.ToJsonString(), sent);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePinOutput>().Subject;
        output.PostId.Should().Be(live["id"]!.GetValue<string>());
        output.PostUrl.Should().Be($"https://www.pinterest.com/pin/{live["id"]!.GetValue<string>()}/");

        // What we sent is what Pinterest stored: board, title, description, link and alt text.
        var body = sent.Single();
        foreach (var field in new[] { "board_id", "title", "description", "link", "alt_text" })
        {
            body[field]!.GetValue<string>().Should().Be(live[field]!.GetValue<string>(), field);
        }
    }

    [Theory]
    [InlineData("images")]
    [InlineData("media")]
    public async Task A_created_pin_without_image_sizes_still_gives_its_id(string removed)
    {
        // The live run saw a sandbox Pin without its image sizes (by the time these replies were captured it had them), so nothing may
        // depend on media.images, or on media at all.
        var live = LivePin();
        if (removed == "media")
        {
            live.Remove("media");
        }
        else
        {
            live["media"]!.AsObject().Remove("images");
        }

        var result = await CreatePin(HttpStatusCode.Created, live.ToJsonString());

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreatePinOutput>().Which.PostId.Should().Be(live["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bad_token_is_the_live_401_and_says_to_generate_a_new_sandbox_token()
    {
        var result = await CreatePin(HttpStatusCode.Unauthorized, LiveFixture.Text("error-401"));

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Contain("Generate a new Sandbox token").And.NotContain(Token);
    }

    [Fact]
    public async Task Testing_the_connection_lists_the_live_board()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Api + "boards?page_size=250").ReturnsResponse(HttpStatusCode.OK, new StringContent(LiveFixture.Text("boards")));

        var result = await new PinterestConnectionType(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), NoOAuth, handler.CreateClientFactory())
            .ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        // The live board's name has brackets of its own; the id is still the last pair.
        result.Message.Should().EndWith("\nUmbSocial test (sandbox) (200000000000000001)");
        handler.VerifyRequest(HttpMethod.Get, r => r.RequestUri!.Query.Contains("bookmark"), Times.Never());
    }

    [Fact]
    public void The_live_account_and_board_agree_with_the_pins()
    {
        // A cross-check of the fixtures themselves: one business account, one board, its three Pins.
        var user = LiveFixture.Object("user-account");
        var board = LiveFixture.Object("boards")["items"]![0]!;
        var pins = LiveFixture.Object("board-pins")["items"]!.AsArray();

        user["account_type"]!.GetValue<string>().Should().Be("BUSINESS");
        user["board_count"]!.GetValue<int>().Should().Be(1);
        board["pin_count"]!.GetValue<int>().Should().Be(pins.Count);
        pins.Should().OnlyContain(p => p!["board_id"]!.GetValue<string>() == board["id"]!.GetValue<string>());
    }
}
