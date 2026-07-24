using FluentAssertions;
using UmbHost.Automate.GitHub.Bindings;

namespace UmbHost.Automate.GitHub.Tests;

public class JsonPathBindingFilterTests
{
    private static object? Apply(string? json, string path) =>
        new JsonPathBindingFilter().Apply(json, new[] { path });

    [Fact]
    public void reads_nested_scalar() =>
        Apply("{\"issue\":{\"number\":123,\"title\":\"Hi\"}}", "issue.number").Should().Be("123");

    [Fact]
    public void reads_string() =>
        Apply("{\"repository\":{\"full_name\":\"umbhost/umbpanel\"}}", "repository.full_name").Should().Be("umbhost/umbpanel");

    [Fact]
    public void reads_array_index() =>
        Apply("{\"commits\":[{\"id\":\"abc\"}]}", "commits[0].id").Should().Be("abc");

    [Fact]
    public void missing_path_returns_null() =>
        Apply("{\"a\":1}", "b.c").Should().BeNull();

    [Fact]
    public void non_json_returns_null() =>
        Apply("not json", "a").Should().BeNull();

    [Fact]
    public void null_value_returns_null() =>
        Apply(null, "a").Should().BeNull();

    [Fact]
    public void alias_is_jsonpath() =>
        new JsonPathBindingFilter().Alias.Should().Be("jsonpath");
}
