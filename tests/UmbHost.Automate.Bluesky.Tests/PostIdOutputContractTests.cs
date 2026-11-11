using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using UmbHost.Automate.Bluesky.Actions;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// UmbSocial binds <c>${ steps.post.uri }</c> as the post's external id, so the property must keep its name and type.
/// </summary>
public class PostIdOutputContractTests
{
    // Mirrors Automate's internal JsonOptions.Default (camelCase), which BindingEvaluator uses to resolve step output bindings.
    private static readonly JsonSerializerOptions AutomateOutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void the_create_output_exposes_the_post_id_under_its_bound_name()
    {
        var property = typeof(CreatePostOutput).GetProperty("Uri");
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));

        var output = new CreatePostOutput();
        property.SetValue(output, "at://did:plc:abc/app.bsky.feed.post/3k2a");
        var json = JsonSerializer.SerializeToNode(output, AutomateOutputJson)!.AsObject();

        json["uri"]!.GetValue<string>().Should().Be("at://did:plc:abc/app.bsky.feed.post/3k2a");
    }
}
