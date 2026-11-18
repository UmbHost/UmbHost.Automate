using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.X.Actions;
using UmbHost.Automate.X.Connection;

namespace UmbHost.Automate.X.Tests;

// Mocked HTTP only. Requests and answers follow X's documentation (checked 2026-11-18):
// - https://docs.x.com/x-api/media/quickstart/best-practices: animated GIFs "<= 15 MB"; larger GIFs need "the chunked upload endpoint with
//   the media_category parameter"; "Pass media_category=tweet_gif".
// - https://docs.x.com/x-api/media/quickstart/media-upload-chunked: POST /2/media/upload/initialize (JSON media_type, total_bytes,
//   media_category), POST /2/media/upload/{id}/append (multipart segment_index, media; segments of at most 5 MB), POST
//   /2/media/upload/{id}/finalize, GET /2/media/upload?command=STATUS&media_id={id} (processing_info.state, check_after_secs).
public class GifUploadTests
{
    private const string Api = "https://api.x.com/2/";
    private const string GifUrl = "https://cdn.example.com/spin.gif";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly XConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static CreatePostAction Sut(Mock<HttpMessageHandler> handler)
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new CreatePostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object)
        {
            StatusPollDelay = TimeSpan.Zero,
        };
    }

    private static void Serve(Mock<HttpMessageHandler> handler, string url, string type, int bytes)
        => handler.SetupRequest(HttpMethod.Get, url).ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent(new byte[bytes]), configure: r =>
            r.Content.Headers.ContentType = new MediaTypeHeaderValue(type));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    /// <summary>A chunked-upload X that records each step; <paramref name="statuses"/> are the STATUS answers in turn.</summary>
    private sealed class FakeX
    {
        public Mock<HttpMessageHandler> Handler { get; } = new();
        public string? Init { get; private set; }
        public List<string> Segments { get; } = [];
        public int StatusChecks { get; private set; }
        public string? Post { get; private set; }

        public FakeX(int gifBytes, string finalize, params string[] statuses)
        {
            Serve(Handler, GifUrl, "image/gif", gifBytes);
            Handler.SetupRequest(HttpMethod.Post, Api + "media/upload/initialize").Returns(async (HttpRequestMessage r, CancellationToken _) =>
            {
                Init = await r.Content!.ReadAsStringAsync();
                return Json("""{"data":{"id":"g7","media_key":"16_g7","expires_after_secs":86400}}""");
            });
            Handler.SetupRequest(HttpMethod.Post, Api + "media/upload/g7/append").Returns(async (HttpRequestMessage r, CancellationToken _) =>
            {
                var form = (MultipartFormDataContent)r.Content!;
                var parts = form.ToList();
                var index = await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "segment_index").ReadAsStringAsync();
                var media = await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "media").ReadAsByteArrayAsync();
                Segments.Add($"{index}:{media.Length}");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            Handler.SetupRequest(HttpMethod.Post, Api + "media/upload/g7/finalize").ReturnsAsync(() => Json(finalize));
            Handler.SetupRequest(HttpMethod.Get, Api + "media/upload?command=STATUS&media_id=g7").ReturnsAsync(() => Json(statuses[Math.Min(StatusChecks++, statuses.Length - 1)]));
            Handler.SetupRequest(HttpMethod.Post, Api + "tweets").Returns(async (HttpRequestMessage r, CancellationToken _) =>
            {
                Post = await r.Content!.ReadAsStringAsync();
                return Json("""{"data":{"id":"1"}}""", HttpStatusCode.Created);
            });
        }
    }

    private static Task<ActionResult> Run(FakeX x)
        => Sut(x.Handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Spin", ImageUrl = GifUrl }, Conn), default);

    [Fact]
    public async Task A_gif_over_5_MB_goes_through_the_chunked_upload_as_tweet_gif_and_waits_for_processing()
    {
        const int size = 9 * 1024 * 1024;
        var x = new FakeX(size, """{"data":{"id":"g7","processing_info":{"state":"pending","check_after_secs":1}}}""",
            """{"data":{"id":"g7","processing_info":{"state":"in_progress","check_after_secs":1,"progress_percent":50}}}""",
            """{"data":{"id":"g7","processing_info":{"state":"succeeded","progress_percent":100}}}""");

        var result = await Run(x);

        result.Status.Should().Be(ActionResultStatus.Success);
        using (var init = JsonDocument.Parse(x.Init!))
        {
            init.RootElement.GetProperty("media_type").GetString().Should().Be("image/gif");
            init.RootElement.GetProperty("total_bytes").GetInt64().Should().Be(size);
            init.RootElement.GetProperty("media_category").GetString().Should().Be("tweet_gif");
        }

        x.Segments.Should().Equal("0:4194304", "1:4194304", "2:1048576");
        x.StatusChecks.Should().Be(2);
        using var post = JsonDocument.Parse(x.Post!);
        post.RootElement.GetProperty("media").GetProperty("media_ids")[0].GetString().Should().Be("g7");
    }

    [Fact]
    public async Task A_small_gif_also_goes_as_tweet_gif_and_is_ready_when_x_sends_no_processing_info()
    {
        var x = new FakeX(200_000, """{"data":{"id":"g7","size":200000}}""");

        var result = await Run(x);

        result.Status.Should().Be(ActionResultStatus.Success);
        x.Segments.Should().Equal("0:200000");
        x.StatusChecks.Should().Be(0);
    }

    [Fact]
    public async Task A_gif_x_fails_to_process_is_refused_before_anything_is_posted()
    {
        var x = new FakeX(200_000, """{"data":{"id":"g7","processing_info":{"state":"pending"}}}""",
            """{"data":{"id":"g7","processing_info":{"state":"failed","error":{"code":1,"name":"InvalidMedia","message":"Unsupported frame count"}}}}""");
        var context = ActionTestHarness.Context(new CreatePostSettings { Text = "Spin", ImageUrl = GifUrl }, Conn);

        var result = await Sut(x.Handler).ExecuteAsync(context, default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Be("X couldn't process the GIF: Unsupported frame count");
        x.Post.Should().BeNull();
        context.LogEntries.Should().ContainSingle(e => e.Message.Contains("InvalidMedia"));
    }

    [Fact]
    public async Task A_gif_over_15_MB_is_refused_without_uploading()
    {
        var x = new FakeX(15 * 1024 * 1024 + 1, "{}");

        var result = await Run(x);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        x.Init.Should().BeNull();
    }

    [Fact]
    public async Task A_still_image_over_5_MB_is_still_refused()
    {
        var handler = new Mock<HttpMessageHandler>();
        Serve(handler, "https://cdn.example.com/big.png", "image/png", 5 * 1024 * 1024 + 1);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreatePostSettings { Text = "Big", ImageUrl = "https://cdn.example.com/big.png" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Be("The image is 5242881 bytes; the limit is 5242880 bytes.");
        handler.VerifyRequest(HttpMethod.Post, Api + "media/upload", Times.Never());
    }
}
