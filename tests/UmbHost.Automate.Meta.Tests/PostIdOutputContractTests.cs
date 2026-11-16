using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// UmbSocial binds <c>${ steps.post.postId }</c> from the Facebook and Instagram actions (both return
/// <see cref="MetaPostOutput"/>) as the post's external id, so the property must keep its name and type.
/// </summary>
public class PostIdOutputContractTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";

    // Mirrors Automate's internal JsonOptions.Default (camelCase), which BindingEvaluator uses to resolve step output bindings.
    private static readonly JsonSerializerOptions AutomateOutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void the_create_output_exposes_the_post_id_under_its_bound_name()
    {
        var property = typeof(MetaPostOutput).GetProperty("PostId");
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));

        var output = new MetaPostOutput();
        property.SetValue(output, "123_456");
        var json = JsonSerializer.SerializeToNode(output, AutomateOutputJson)!.AsObject();

        json["postId"]!.GetValue<string>().Should().Be("123_456");
    }

    [Fact]
    public async Task a_photo_post_reports_post_id_not_the_photo_id()
    {
        var handler = new Mock<HttpMessageHandler>();
        // POST /{page-id}/photos answers with the photo's id and the feed post's post_id:
        // https://developers.facebook.com/docs/graph-api/reference/page/photos/#publish
        handler.SetupRequest(HttpMethod.Post, Graph + "123/photos")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"id":"9","post_id":"123_789"}"""));
        handler.SetupRequest(HttpMethod.Get, Graph + "123_789?fields=permalink_url")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"permalink_url":"https://www.facebook.com/123/posts/789","id":"123_789"}"""));

        var sut = new CreateFacebookPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory());
        var result = await sut.ExecuteAsync(ActionTestHarness.Context(
            new CreateFacebookPostSettings { Message = "Pic", ImageUrl = "https://cdn.example.com/a.jpg" },
            new MetaConnectionSettings { PageId = "123", PageAccessToken = "ptok" }), default);

        result.OutputData.Should().BeOfType<MetaPostOutput>().Which.PostId.Should().Be("123_789");
    }
}
