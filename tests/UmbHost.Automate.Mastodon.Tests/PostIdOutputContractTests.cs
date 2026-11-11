using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using UmbHost.Automate.Mastodon.Actions;

namespace UmbHost.Automate.Mastodon.Tests;

/// <summary>
/// UmbSocial binds <c>${ steps.post.statusId }</c> as the post's external id, so the property must keep its name and type.
/// </summary>
public class PostIdOutputContractTests
{
    // Mirrors Automate's internal JsonOptions.Default (camelCase), which BindingEvaluator uses to resolve step output bindings.
    private static readonly JsonSerializerOptions AutomateOutputJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void the_create_output_exposes_the_post_id_under_its_bound_name()
    {
        var property = typeof(CreateStatusOutput).GetProperty("StatusId");
        property.Should().NotBeNull();
        property!.PropertyType.Should().Be(typeof(string));

        var output = new CreateStatusOutput();
        property.SetValue(output, "109372843234");
        var json = JsonSerializer.SerializeToNode(output, AutomateOutputJson)!.AsObject();

        json["statusId"]!.GetValue<string>().Should().Be("109372843234");
    }
}
