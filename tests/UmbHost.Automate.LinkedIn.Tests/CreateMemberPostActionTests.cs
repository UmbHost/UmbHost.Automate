using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// Posting as the connected member. Mocked only: LinkedIn is never called. The author is <c>urn:li:person:{sub}</c> from
/// <c>GET /v2/userinfo</c> (https://learn.microsoft.com/en-us/linkedin/consumer/integrations/self-serve/sign-in-with-linkedin-v2); the post
/// goes through the Posts API like the company page's; an image's owner is the member, whose token can't read the image's status
/// (https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/images-api, Permissions).
/// </summary>
public class CreateMemberPostActionTests
{
    private const string Rest = "https://api.linkedin.com/rest/";
    private const string UserInfo = "https://api.linkedin.com/v2/userinfo";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInProfileConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static CreateMemberPostAction Sut(Mock<HttpMessageHandler> handler, string? token = "tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new CreateMemberPostAction(
            ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), credentials.Object)
        {
            ImagePollDelay = TimeSpan.Zero,
        };
    }

    private static Mock<HttpMessageHandler> Member(string body = """{"sub":"782bbtaQ","name":"Jane Doe"}""")
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.OK, body);
        return handler;
    }

    private static void PostCreated(Mock<HttpMessageHandler> handler, List<string>? bodies = null)
        => handler.SetupRequest(HttpMethod.Post, Rest + "posts").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            bodies?.Add(await r.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Add("x-restli-id", "urn:li:share:7002");
            return response;
        });

    private static void ImageUpload(Mock<HttpMessageHandler> handler, List<string> initBodies)
    {
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/a.png")
               .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2]), configure: r =>
                   r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png"));
        handler.SetupRequest(HttpMethod.Post, Rest + "images?action=initializeUpload").Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            initBodies.Add(await r.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":{"uploadUrl":"https://www.linkedin.com/dms-uploads/x","image":"urn:li:image:C4E"}}"""),
            };
        });
        handler.SetupRequest(HttpMethod.Put, "https://www.linkedin.com/dms-uploads/x").ReturnsResponse(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Posts_as_the_member_named_by_userinfo()
    {
        var handler = Member();
        var bodies = new List<string>();
        PostCreated(handler, bodies);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings
        {
            Text = "New (beta) #Umbraco",
            LinkUrl = "https://umbhost.net/blog",
            LinkTitle = "Blog",
            LinkDescription = "News",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateMemberPostOutput>().Subject;
        output.PostUrn.Should().Be("urn:li:share:7002");
        output.PostUrl.Should().Be("https://www.linkedin.com/feed/update/urn:li:share:7002/");
        output.Warning.Should().BeNull();

        using var doc = JsonDocument.Parse(bodies.Single());
        doc.RootElement.GetProperty("author").GetString().Should().Be("urn:li:person:782bbtaQ");
        doc.RootElement.GetProperty("commentary").GetString().Should().Be(@"New \(beta\) #Umbraco");
        var article = doc.RootElement.GetProperty("content").GetProperty("article");
        article.GetProperty("source").GetString().Should().Be("https://umbhost.net/blog");
        article.GetProperty("title").GetString().Should().Be("Blog");
        article.GetProperty("description").GetString().Should().Be("News");
    }

    [Fact]
    public async Task Userinfo_goes_without_the_rest_version_headers_and_the_post_with_them()
    {
        var handler = Member();
        PostCreated(handler);

        await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        handler.VerifyRequest(HttpMethod.Get, UserInfo, r =>
            r.Headers.Authorization!.Parameter == "tok" && !r.Headers.Contains("LinkedIn-Version"));
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", r =>
            r.Headers.GetValues("LinkedIn-Version").Single() == "202610"
            && r.Headers.GetValues("X-Restli-Protocol-Version").Single() == "2.0.0"
            && r.Headers.Authorization!.Parameter == "tok");
    }

    [Fact]
    public async Task An_image_is_owned_by_the_member_and_carries_its_alt_text()
    {
        var handler = Member();
        var inits = new List<string>();
        ImageUpload(handler, inits);
        handler.SetupRequest(HttpMethod.Get, Rest + "images/urn%3Ali%3Aimage%3AC4E")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status":"AVAILABLE"}"""));
        var bodies = new List<string>();
        PostCreated(handler, bodies);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings
        {
            Text = "Pic", ImageUrl = "https://cdn.example.com/a.png", ImageAltText = "Rack",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        JsonNode.Parse(inits.Single())!["initializeUploadRequest"]!["owner"]!.GetValue<string>().Should().Be("urn:li:person:782bbtaQ");
        using var doc = JsonDocument.Parse(bodies.Single());
        var media = doc.RootElement.GetProperty("content").GetProperty("media");
        media.GetProperty("id").GetString().Should().Be("urn:li:image:C4E");
        media.GetProperty("altText").GetString().Should().Be("Rack");
    }

    /// <summary>"tokens with only w_member_social permissions would be unable to perform a GET call for rest/images" (Images API).</summary>
    [Fact]
    public async Task An_image_whose_status_a_member_token_cant_read_is_still_posted()
    {
        var handler = Member();
        ImageUpload(handler, []);
        handler.SetupRequest(HttpMethod.Get, Rest + "images/urn%3Ali%3Aimage%3AC4E")
               .ReturnsResponse(HttpStatusCode.Forbidden, new StringContent("""{"status":403}"""));
        var bodies = new List<string>();
        PostCreated(handler, bodies);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings
        {
            Text = "Pic", ImageUrl = "https://cdn.example.com/a.png",
        }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        JsonNode.Parse(bodies.Single())!["content"]!["media"]!["id"]!.GetValue<string>().Should().Be("urn:li:image:C4E");
        handler.VerifyRequest(HttpMethod.Get, Rest + "images/urn%3Ali%3Aimage%3AC4E", Times.Once());
    }

    [Fact]
    public async Task A_failed_image_still_stops_the_post()
    {
        var handler = Member();
        ImageUpload(handler, []);
        handler.SetupRequest(HttpMethod.Get, Rest + "images/urn%3Ali%3Aimage%3AC4E")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"status":"PROCESSING_FAILED"}"""));
        PostCreated(handler);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings
        {
            Text = "Pic", ImageUrl = "https://cdn.example.com/a.png",
        }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Never());
    }

    [Fact]
    public async Task The_first_comment_is_made_as_the_member()
    {
        var handler = Member();
        PostCreated(handler);
        string? body = null;
        handler.SetupRequest(HttpMethod.Post, Rest + "socialActions/urn%3Ali%3Ashare%3A7002/comments")
               .Returns(async (HttpRequestMessage r, CancellationToken _) =>
               {
                   body = await r.Content!.ReadAsStringAsync();
                   return new HttpResponseMessage(HttpStatusCode.Created);
               });

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = "Hello", FirstComment = "More at https://umbhost.net" }, Conn), default);

        result.OutputData.Should().BeOfType<CreateMemberPostOutput>().Which.Warning.Should().BeNull();
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.GetProperty("actor").GetString().Should().Be("urn:li:person:782bbtaQ");
        doc.RootElement.GetProperty("object").GetString().Should().Be("urn:li:share:7002");
    }

    /// <summary>The Comments API lists w_member_social_feed for members, the Posts API w_member_social: a refusal must not lose the post.</summary>
    [Fact]
    public async Task A_refused_first_comment_leaves_the_post_published_with_a_warning()
    {
        var handler = Member();
        PostCreated(handler);
        handler.SetupRequest(HttpMethod.Post, Rest + "socialActions/urn%3Ali%3Ashare%3A7002/comments")
               .ReturnsResponse(HttpStatusCode.Forbidden, new StringContent("ACCESS_DENIED"));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = "Hello", FirstComment = "c" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateMemberPostOutput>().Subject;
        output.PostUrn.Should().Be("urn:li:share:7002");
        output.Warning.Should().Contain("first comment could not be added").And.Contain("403");
    }

    [Fact]
    public async Task Expired_token_asks_for_reauthorisation_before_any_call()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler, token: null).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        result.Exception!.Message.Should().Contain("Re-authorise");
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task A_refused_token_at_userinfo_asks_for_reauthorisation_and_posts_nothing()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.Unauthorized);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Never());
    }

    [Fact]
    public async Task An_app_without_sign_in_fails_the_lookup_and_posts_nothing()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.Forbidden, new StringContent("ACCESS_DENIED"));

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        result.Status.Should().NotBe(ActionResultStatus.Success);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Never());
    }

    [Fact]
    public async Task A_userinfo_without_a_sub_posts_nothing()
    {
        var handler = Member("""{"name":"Jane Doe"}""");

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Never());
    }

    [Fact]
    public async Task Link_without_title_is_rejected()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = "x", LinkUrl = "https://a.io" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Link_and_image_together_are_rejected()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = "x", LinkUrl = "https://a.io", LinkTitle = "t", ImageUrl = "https://a.io/i.png" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Empty_text_is_rejected()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = " " }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Without_a_connection_it_fails_as_authentication()
    {
        var result = await Sut(new Mock<HttpMessageHandler>()).ExecuteAsync(ActionTestHarness.Context(
            new CreateMemberPostSettings { Text = "x" }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }

    [Fact]
    public async Task Server_error_on_create_is_not_retried()
    {
        var handler = Member();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").ReturnsResponse(HttpStatusCode.InternalServerError);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(new CreateMemberPostSettings { Text = "x" }, Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Once());
    }

    [Fact]
    public void The_output_exposes_the_post_id_under_the_name_UmbSocial_binds()
    {
        var json = JsonSerializer.SerializeToNode(
            new CreateMemberPostOutput { PostUrn = "urn:li:share:1" }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        json["postUrn"]!.GetValue<string>().Should().Be("urn:li:share:1");
    }

    [Fact]
    public void The_action_runs_on_the_profile_connection()
    {
        var attribute = typeof(CreateMemberPostAction).GetCustomAttributes(typeof(ActionAttribute), false).Cast<ActionAttribute>().Single();

        attribute.Alias.Should().Be("linkedin.createMemberPost");
        attribute.ConnectionTypeAlias.Should().Be("linkedin-profile");
    }
}
