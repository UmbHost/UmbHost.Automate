using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Tests;

public class CreateStatusActionTests
{
    private const string Api = "https://mastodon.social/api/";

    private static readonly MastodonConnectionSettings Conn = new()
    {
        InstanceUrl = "https://mastodon.social",
        AccessToken = "tok",
    };

    private static CreateStatusAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options())
        {
            MediaPollDelay = TimeSpan.Zero,
        };

    private static void ServeImage(Mock<HttpMessageHandler> handler)
        => handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/pic.jpg")
                  .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), configure: r =>
                      r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"));

    [Fact]
    public async Task Posts_status_with_idempotency_key_from_run_and_step()
    {
        var handler = new Mock<HttpMessageHandler>();
        HttpRequestMessage? sent = null;
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   sent = r;
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent("""{"id":"1101","url":"https://mastodon.social/@umbhost/1101"}"""),
                   };
               });
        var context = ActionTestHarness.Context(new CreateStatusSettings { Text = "Hello", Visibility = "unlisted" }, Conn);

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateStatusOutput>().Subject;
        output.StatusId.Should().Be("1101");
        output.PostUrl.Should().Be("https://mastodon.social/@umbhost/1101");

        sent!.Headers.Authorization!.Parameter.Should().Be("tok");
        sent.Headers.GetValues("Idempotency-Key").Single().Should().Be($"{context.RunId}:{context.StepId}");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Hello");
        doc.RootElement.GetProperty("visibility").GetString().Should().Be("unlisted");
    }

    [Fact]
    public async Task Uploads_image_waits_for_processing_and_attaches_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServeImage(handler);
        handler.SetupRequest(HttpMethod.Post, Api + "v2/media")
               .ReturnsResponse(HttpStatusCode.Accepted, new StringContent("""{"id":"77","url":null}"""));
        handler.SetupRequestSequence(HttpMethod.Get, Api + "v1/media/77")
               .ReturnsResponse(HttpStatusCode.PartialContent, new StringContent("""{"id":"77","url":null}"""))
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"id":"77","url":"https://files/x.jpg"}"""));
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"1","url":"u"}""") };
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "Pic",
            ImageUrl = "https://cdn.example.com/pic.jpg",
            ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("media_ids")[0].GetString().Should().Be("77");
    }

    [Fact]
    public async Task Server_error_on_post_is_retryable_because_of_the_idempotency_key()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses").ReturnsResponse(HttpStatusCode.BadGateway);

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreateStatusSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
    }

    [Fact]
    public async Task Rejected_status_is_a_validation_failure()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .ReturnsResponse(HttpStatusCode.UnprocessableEntity, new StringContent("""{"error":"Text too long"}"""));

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreateStatusSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Unknown_visibility_is_a_validation_failure()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreateStatusSettings { Text = "x", Visibility = "everyone" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task Missing_connection_is_an_authentication_failure()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(
            ActionTestHarness.Context(new CreateStatusSettings { Text = "x" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }
}
