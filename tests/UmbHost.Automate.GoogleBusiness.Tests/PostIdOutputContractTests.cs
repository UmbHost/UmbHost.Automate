using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using UmbHost.Automate.GoogleBusiness.Actions;

namespace UmbHost.Automate.GoogleBusiness.Tests;

/// <summary>
/// UmbSocial binds <c>${ steps.post.postId }</c> as the post's external id, so the property must keep its name and type.
/// </summary>
public class PostIdOutputContractTests
{
    // Mirrors Automate's internal JsonOptions.Default (camelCase), which BindingEvaluator uses to resolve step output bindings.
    private static readonly JsonSerializerOptions AutomateOutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void the_create_output_exposes_the_post_id_under_its_bound_name()
    {
        var property = typeof(CreateUpdateOutput).GetProperty("PostId");
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));

        var output = new CreateUpdateOutput();
        property.SetValue(output, "accounts/1/locations/2/localPosts/3");
        var json = JsonSerializer.SerializeToNode(output, AutomateOutputJson)!.AsObject();

        json["postId"]!.GetValue<string>().Should().Be("accounts/1/locations/2/localPosts/3");
    }
}
