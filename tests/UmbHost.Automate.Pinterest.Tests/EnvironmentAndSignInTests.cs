using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Actions;
using UmbHost.Automate.Pinterest.Connection;

namespace UmbHost.Automate.Pinterest.Tests;

// Environment (Production / Sandbox) picks the host for every call; Sign-in (OAuth / Access token) picks where the bearer token comes
// from. Sandbox is the same API on api-sandbox.pinterest.com, with its own tokens and boards:
// https://developers.pinterest.com/docs/developer-tools/sandbox/. Mocked HTTP only.
public class EnvironmentAndSignInTests
{
    private const string OAuthToken = "pina_oauth";
    private const string StaticToken = "pina_static";
    private static readonly Guid CredentialsId = Guid.NewGuid();

    public static TheoryData<PinterestEnvironment, PinterestSignIn, string, string> Supported => new()
    {
        { PinterestEnvironment.Production, PinterestSignIn.OAuth, "https://api.pinterest.com/v5/", OAuthToken },
        { PinterestEnvironment.Production, PinterestSignIn.AccessToken, "https://api.pinterest.com/v5/", StaticToken },
        { PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken, "https://api-sandbox.pinterest.com/v5/", StaticToken },
    };

    private static PinterestConnectionSettings Conn(PinterestEnvironment environment, PinterestSignIn signIn, string? accessToken = StaticToken)
        => new() { Environment = environment, SignIn = signIn, OAuthCredentialsId = CredentialsId, AccessToken = accessToken };

    private static Mock<IOAuthCredentialsService> Credentials()
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(OAuthToken);
        return credentials;
    }

    private static PinterestConnectionType Connection(Mock<HttpMessageHandler> handler, Mock<IOAuthCredentialsService>? credentials = null)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), (credentials ?? Credentials()).Object, handler.CreateClientFactory());

    private static ActionContext PinContext(PinterestConnectionSettings conn) => ActionTestHarness.Context(
        new CreatePinSettings { BoardId = "549755885175", ImageUrl = "https://example.com/media/menu.jpg", Title = "Autumn menu" }, conn);

    private static ActionContext MetricsContext(PinterestConnectionSettings conn)
        => ActionTestHarness.Context(new GetPostMetricsSettings { Ids = "[\"813744226420795884\"]" }, conn);

    [Fact]
    public void A_stored_connection_without_the_new_settings_is_production_oauth()
    {
        var settings = JsonSerializer.Deserialize<PinterestConnectionSettings>($$"""{"OAuthCredentialsId":"{{CredentialsId}}"}""")!;

        settings.Environment.Should().Be(PinterestEnvironment.Production);
        settings.SignIn.Should().Be(PinterestSignIn.OAuth);
        // No default secret reference: Automate resolves every field, so one would fail OAuth connections on sites without the secret.
        settings.AccessToken.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task Testing_the_connection_lists_boards_on_the_environment_host_with_its_token(
        PinterestEnvironment environment, PinterestSignIn signIn, string api, string token)
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        handler.SetupRequest(HttpMethod.Get, api + "boards?page_size=250")
            .Returns((HttpRequestMessage r, CancellationToken _) =>
            {
                sent = r;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"items":[{"id":"1","name":"Recipes"}],"bookmark":null}""") });
            });

        var result = await Connection(handler).ValidateAsync(Conn(environment, signIn), default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().StartWith($"{environment}, {(signIn == PinterestSignIn.OAuth ? "OAuth" : "access token")}: ");
        result.Message.Should().Contain("Recipes (1)");
        sent!.Headers.Authorization!.ToString().Should().Be("Bearer " + token);
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task Create_pin_posts_to_the_environment_host_with_its_token(
        PinterestEnvironment environment, PinterestSignIn signIn, string api, string token)
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        handler.SetupRequest(HttpMethod.Post, api + "pins")
            .Returns((HttpRequestMessage r, CancellationToken _) =>
            {
                sent = r;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"813744226420795884"}""") });
            });

        var result = await new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(PinContext(Conn(environment, signIn)), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        sent!.Headers.Authorization!.ToString().Should().Be("Bearer " + token);
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public async Task Metrics_read_from_the_environment_host_with_its_token(
        PinterestEnvironment environment, PinterestSignIn signIn, string api, string token)
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        handler.SetupRequest(HttpMethod.Get, api + "pins/813744226420795884?pin_metrics=true")
            .Returns((HttpRequestMessage r, CancellationToken _) =>
            {
                sent = r;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"813744226420795884","pin_metrics":{"lifetime_metrics":{"impression":5}}}""") });
            });

        var result = await new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(MetricsContext(Conn(environment, signIn)), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        sent!.Headers.Authorization!.ToString().Should().Be("Bearer " + token);
    }

    [Fact]
    public async Task An_access_token_never_asks_for_the_oauth_credential()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.OK, """{"items":[{"id":"1","name":"Recipes"}],"bookmark":null}""");
        var credentials = new Mock<IOAuthCredentialsService>(MockBehavior.Strict);
        var conn = Conn(PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken);
        conn.OAuthCredentialsId = null;

        var result = await Connection(handler, credentials).ValidateAsync(conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().StartWith("Sandbox, access token: ");
    }

    [Fact]
    public async Task Sandbox_with_oauth_is_refused_without_a_call()
    {
        var handler = new Mock<HttpMessageHandler>();
        var conn = Conn(PinterestEnvironment.Sandbox, PinterestSignIn.OAuth);

        var test = await Connection(handler).ValidateAsync(conn, default);
        var pin = await new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(PinContext(conn), default);
        var metrics = await new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(MetricsContext(conn), default);

        test.Status.Should().Be(ConnectionValidationStatus.Failure);
        test.Message.Should().StartWith("Sandbox, OAuth: ").And.Contain("Access token");
        pin.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        metrics.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task A_missing_access_token_is_authentication_without_a_call(string? token)
    {
        var handler = new Mock<HttpMessageHandler>();
        var conn = Conn(PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken, token);

        var test = await Connection(handler).ValidateAsync(conn, default);
        var pin = await new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(PinContext(conn), default);

        test.Status.Should().Be(ConnectionValidationStatus.Failure);
        test.Message.Should().Contain("No Pinterest access token");
        pin.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task A_rejected_access_token_says_to_regenerate_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        // The sandbox's real answer to a bad token (Fixtures/Live, 2026-11-18).
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.Unauthorized, LiveFixture.Text("error-401"));
        var conn = Conn(PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken);

        var test = await Connection(handler).ValidateAsync(conn, default);
        var pin = await new CreatePinAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(PinContext(conn), default);
        var metrics = await new GetPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object)
            .ExecuteAsync(MetricsContext(conn), default);

        test.Status.Should().Be(ConnectionValidationStatus.Failure);
        test.Message.Should().Contain("expired").And.Contain("Generate a new Sandbox token");
        foreach (var result in new[] { pin, metrics })
        {
            result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
            result.Exception.Should().BeOfType<UnauthorizedAccessException>();
            result.Exception!.Message.Should().Contain("Generate a new Sandbox token");
            result.Exception.Message.Should().NotContain(StaticToken);
        }
    }

    [Fact]
    public async Task The_board_walk_follows_the_bookmark_past_empty_pages()
    {
        const string api = "https://api-sandbox.pinterest.com/v5/boards?page_size=250";
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, api).ReturnsResponse(HttpStatusCode.OK, """{"items":[],"bookmark":"b2"}""");
        handler.SetupRequest(HttpMethod.Get, api + "&bookmark=b2").ReturnsResponse(HttpStatusCode.OK, """{"items":[],"bookmark":"b3"}""");
        handler.SetupRequest(HttpMethod.Get, api + "&bookmark=b3")
            .ReturnsResponse(HttpStatusCode.OK, """{"items":[{"id":"7","name":"Sandbox board"}],"bookmark":null}""");

        var result = await Connection(handler).ValidateAsync(Conn(PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken), default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().StartWith("Sandbox, access token: ").And.Contain("Sandbox board (7)");
    }

    [Fact]
    public async Task The_board_walk_stops_at_the_page_cap()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.OK, """{"items":[],"bookmark":"forever"}""");

        var result = await Connection(handler).ValidateAsync(Conn(PinterestEnvironment.Sandbox, PinterestSignIn.AccessToken), default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Contain($"in {PinterestConnectionType.MaxPages} pages");
        handler.VerifyAnyRequest(Times.Exactly(PinterestConnectionType.MaxPages));
    }
}
