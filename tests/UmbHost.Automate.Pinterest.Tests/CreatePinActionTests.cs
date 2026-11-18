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

// Mocked HTTP only: nothing here reaches the real Pinterest API. Requests and responses follow Pinterest's published API v5
// description (https://github.com/pinterest/api-description/blob/main/v5/openapi.yaml): POST /v5/pins takes a PinCreate
// (board_id, title ≤ 100, description ≤ 800, link ≤ 2048, alt_text ≤ 500, media_source {source_type: image_url, url}) and answers
// 201 with the Pin; GET /v5/boards answers a page of boards ({items: [{id, name}], bookmark}).
public class CreatePinActionTests
{
    private const string Api = "https://api.pinterest.com/v5/";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly PinterestConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static Mock<IOAuthCredentialsService> Credentials(string? token = "pina_tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return credentials;
    }

    private static CreatePinAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object);

    private static CreatePinSettings Pin(Action<CreatePinSettings>? change = null)
    {
        var settings = new CreatePinSettings
        {
            BoardId = "549755885175",
            Title = "Autumn menu",
            Description = "Our new autumn menu #food",
            Link = "https://example.com/menu",
            ImageUrl = "https://example.com/media/menu.jpg",
            ImageAltText = "A bowl of soup",
        };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task Pinterest_creates_a_pin_on_the_chosen_board()
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        JsonElement body = default;
        handler.SetupRequest(HttpMethod.Post, Api + "pins").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            sent = r;
            body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"813744226420795884","board_id":"549755885175"}""") };
        });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreatePinOutput>().Subject;
        output.PostId.Should().Be("813744226420795884");
        output.PostUrl.Should().Be("https://www.pinterest.com/pin/813744226420795884/");
        sent!.Headers.Authorization!.ToString().Should().Be("Bearer pina_tok");
        body.GetProperty("board_id").GetString().Should().Be("549755885175");
        body.GetProperty("title").GetString().Should().Be("Autumn menu");
        body.GetProperty("description").GetString().Should().Be("Our new autumn menu #food");
        body.GetProperty("link").GetString().Should().Be("https://example.com/menu");
        body.GetProperty("alt_text").GetString().Should().Be("A bowl of soup");
        body.GetProperty("media_source").GetProperty("source_type").GetString().Should().Be("image_url");
        body.GetProperty("media_source").GetProperty("url").GetString().Should().Be("https://example.com/media/menu.jpg");
    }

    [Fact]
    public async Task Pinterest_leaves_out_what_the_post_does_not_have()
    {
        var handler = new Mock<HttpMessageHandler>();
        JsonElement body = default;
        handler.SetupRequest(HttpMethod.Post, Api + "pins").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"id":"1"}""") };
        });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(p => (p.Title, p.Link, p.ImageAltText) = (null, "", null)), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        body.TryGetProperty("title", out _).Should().BeFalse();
        body.TryGetProperty("link", out _).Should().BeFalse();
        body.TryGetProperty("alt_text", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Pinterest_without_an_image_is_refused()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(p => p.ImageUrl = " "), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Contain("image");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Theory]
    [InlineData("title", 101)]
    [InlineData("description", 801)]
    [InlineData("alt", 501)]
    public async Task Pinterest_refuses_text_over_its_limits(string field, int length)
    {
        var handler = new Mock<HttpMessageHandler>();
        var text = new string('a', length);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(p =>
        {
            if (field == "title") p.Title = text;
            else if (field == "description") p.Description = text;
            else p.ImageAltText = text;
        }), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task Pinterest_refuses_a_board_id_that_is_not_numeric()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(p => p.BoardId = "../users"), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task A_rejected_pin_is_a_definite_failure()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "pins")
            .ReturnsResponse(HttpStatusCode.BadRequest, """{"code":1,"message":"Invalid board"}""");

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Contain("Invalid board");
    }

    [Fact]
    public async Task An_unconfirmed_pin_may_exist_and_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "pins").ReturnsResponse(HttpStatusCode.BadGateway);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Pin(), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    private static PinterestConnectionType Connection(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), Credentials().Object, handler.CreateClientFactory());

    [Fact]
    public async Task Testing_the_connection_lists_the_boards_by_name_and_id()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Api + "boards?page_size=250")
            .ReturnsResponse(HttpStatusCode.OK, """{"items":[{"id":"549755885175","name":"Recipes"}],"bookmark":"b2"}""");
        handler.SetupRequest(HttpMethod.Get, Api + "boards?page_size=250&bookmark=b2")
            .ReturnsResponse(HttpStatusCode.OK, """{"items":[{"id":"549755885176","name":"Shop"}],"bookmark":null}""");

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Contain("Recipes (549755885175)\nShop (549755885176)");
    }

    [Fact]
    public async Task A_connection_with_no_boards_warns()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Api + "boards?page_size=250").ReturnsResponse(HttpStatusCode.OK, """{"items":[],"bookmark":null}""");

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Be("Production, OAuth: no boards found. Create one in Pinterest first.");
    }
}
