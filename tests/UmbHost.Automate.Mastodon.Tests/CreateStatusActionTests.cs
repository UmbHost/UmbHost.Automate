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

// Answers are the real ones umbracocommunity.social (Mastodon 4.7.5) gave on 2026-11-18 (Fixtures/Live) where we saw that case live:
// the Status a post returns, its MediaAttachment, and the 401 for a bad token. Cases we didn't see live are marked docs-based
// (https://docs.joinmastodon.org/methods/statuses/, https://docs.joinmastodon.org/methods/media/).
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
                   // The whole Status the instance answered for the live unlisted post.
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LiveFixture.Text("status-unlisted")) };
               });
        var context = ActionTestHarness.Context(new CreateStatusSettings { Text = "Hello", Visibility = "unlisted" }, Conn);

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateStatusOutput>().Subject;
        output.StatusId.Should().Be("110000000000000006");
        output.PostUrl.Should().Be("https://umbracocommunity.social/@example/110000000000000006");

        sent!.Headers.Authorization!.Parameter.Should().Be("tok");
        sent.Headers.GetValues("Idempotency-Key").Single().Should().Be($"{context.RunId}:{context.StepId}");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Hello");
        doc.RootElement.GetProperty("visibility").GetString().Should().Be("unlisted");
    }

    /// <summary>The id of the live image post's MediaAttachment (the entity <c>POST /api/v2/media</c> answers with).</summary>
    private const string LiveMediaId = "120000000000000001";

    private static string LiveAttachment() => LiveFixture.Object("status-image")["media_attachments"]![0]!.ToJsonString();

    /// <summary>
    /// The live attachment as it reads while the instance is still processing it. Docs-based: the docs say a 202 (and the 206 from
    /// <c>GET /api/v1/media/:id</c>) carries <c>url</c> null until processing is done; we didn't see that live.
    /// </summary>
    private static string ProcessingAttachment()
    {
        var attachment = LiveFixture.Object("status-image")["media_attachments"]![0]!.AsObject();
        attachment["url"] = null;
        return attachment.ToJsonString();
    }

    [Fact]
    public async Task Uploads_image_waits_for_processing_and_attaches_it()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServeImage(handler);
        handler.SetupRequest(HttpMethod.Post, Api + "v2/media")
               .ReturnsResponse(HttpStatusCode.Accepted, new StringContent(ProcessingAttachment()));
        handler.SetupRequestSequence(HttpMethod.Get, Api + $"v1/media/{LiveMediaId}")
               .ReturnsResponse(HttpStatusCode.PartialContent, new StringContent(ProcessingAttachment()))
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(LiveAttachment()));
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LiveFixture.Text("status-image")) };
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "Pic",
            ImageUrl = "https://cdn.example.com/pic.jpg",
            ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateStatusOutput>().Which.StatusId.Should().Be("110000000000000002");
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("media_ids")[0].GetString().Should().Be(LiveMediaId);
    }

    [Fact]
    public async Task An_image_the_instance_processed_straight_away_is_attached_without_waiting()
    {
        var handler = new Mock<HttpMessageHandler>();
        ServeImage(handler);
        string? description = null;
        handler.SetupRequest(HttpMethod.Post, Api + "v2/media")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   var part = ((MultipartFormDataContent)r.Content!).Single(p => p.Headers.ContentDisposition!.Name == "description");
                   description = await part.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LiveAttachment()) };
               });
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LiveFixture.Text("status-image")) };
               });
        var altText = LiveFixture.Object("status-image")["media_attachments"]![0]!["description"]!.GetValue<string>();

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "Pic",
            ImageUrl = "https://cdn.example.com/pic.jpg",
            ImageAltText = altText,
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        handler.VerifyRequest(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/media/"), Times.Never());
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("media_ids")[0].GetString().Should().Be(LiveMediaId);

        // The alt text goes up as the upload's description, which is where the live post's alt text came back.
        description.Should().Be(altText);
    }

    [Fact]
    public async Task A_thread_behind_a_content_warning_reads_as_the_live_thread()
    {
        var handler = new Mock<HttpMessageHandler>();
        var bodies = new List<string>();
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   bodies.Add(await r.Content!.ReadAsStringAsync());
                   var answer = bodies.Count == 1 ? "status-content-warning-thread-start" : "status-thread-reply";
                   return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LiveFixture.Text(answer)) };
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateStatusSettings
        {
            Text = "Part 1",
            ThreadParts = """["Part 2"]""",
            ContentWarning = "UmbSocial test post",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateStatusOutput>().Subject;
        output.StatusId.Should().Be("110000000000000004");
        output.ThreadUrls.Should().Equal(
            "https://umbracocommunity.social/@example/110000000000000004",
            "https://umbracocommunity.social/@example/110000000000000005");

        // The reply goes to the first status, which is what the live reply's in_reply_to_id points at, behind the same warning.
        using var reply = JsonDocument.Parse(bodies[1]);
        reply.RootElement.GetProperty("in_reply_to_id").GetString().Should().Be(
            LiveFixture.Object("status-thread-reply")["in_reply_to_id"]!.GetValue<string>());
        reply.RootElement.GetProperty("spoiler_text").GetString().Should().Be(
            LiveFixture.Object("status-thread-reply")["spoiler_text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bad_token_is_an_authentication_failure_in_mastodons_own_words()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent(LiveFixture.Text("error-401-invalid-token")));
        var context = ActionTestHarness.Context(new CreateStatusSettings { Text = "x" }, Conn);

        var result = await Sut(handler).ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Be("The access token is invalid");
        context.LogEntries.Should().ContainSingle().Which.Message.Should().StartWith("Mastodon returned 401: {");
    }

    // Docs-based: no 5xx was seen live.
    [Fact]
    public async Task Server_error_on_post_is_retryable_because_of_the_idempotency_key()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Api + "v1/statuses").ReturnsResponse(HttpStatusCode.BadGateway);

        var result = await Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new CreateStatusSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ServiceUnavailable);
    }

    // Docs-based: we haven't seen a 422 live (https://docs.joinmastodon.org/methods/statuses/#create).
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
