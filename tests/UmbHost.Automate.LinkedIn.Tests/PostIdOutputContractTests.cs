using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using UmbHost.Automate.LinkedIn.Actions;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// UmbSocial binds <c>${ steps.post.postUrn }</c> as the post's external id, so the property must keep its name and type.
/// </summary>
public class PostIdOutputContractTests
{
    // Mirrors Automate's internal JsonOptions.Default (camelCase), which BindingEvaluator uses to resolve step output bindings.
    private static readonly JsonSerializerOptions AutomateOutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void the_create_output_exposes_the_post_id_under_its_bound_name()
    {
        var property = typeof(CreateOrganizationPostOutput).GetProperty("PostUrn");
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));

        var output = new CreateOrganizationPostOutput();
        property.SetValue(output, "urn:li:share:6844785523593134080");
        var json = JsonSerializer.SerializeToNode(output, AutomateOutputJson)!.AsObject();

        json["postUrn"]!.GetValue<string>().Should().Be("urn:li:share:6844785523593134080");
    }
}
