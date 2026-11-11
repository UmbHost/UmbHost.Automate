using FluentAssertions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The shared ids setting every provider's <c>getPostMetrics</c> action reads (UmbSocial binds it as <c>${ trigger.ids | json }</c>).
/// </summary>
public class PostMetricsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"at://x\"")]
    [InlineData("[]")]
    [InlineData("[\"a\", 1]")]
    [InlineData("[\"a\", null]")]
    [InlineData("[\"a\", \"\"]")]
    [InlineData("[\"a\", \"   \"]")]
    public void ids_must_be_a_json_array_of_one_to_one_hundred(string? json)
        => PostMetricsIds.Parse(json).Should().BeNull();

    [Fact]
    public void ids_must_be_a_json_array_of_one_to_one_hundred_and_no_more()
    {
        var hundred = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(i => $"id{i}"));
        var hundredAndOne = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 101).Select(i => $"id{i}"));

        PostMetricsIds.Parse(hundred).Should().HaveCount(100);
        PostMetricsIds.Parse(hundredAndOne).Should().BeNull();
        PostMetricsIds.Parse("[\"a\",\"b\",\"c\"]", max: 2).Should().BeNull();
    }

    [Fact]
    public void ids_longer_than_500_characters_are_refused()
    {
        PostMetricsIds.Parse($"[\"{new string('x', 500)}\"]").Should().ContainSingle();
        PostMetricsIds.Parse($"[\"{new string('x', 501)}\"]").Should().BeNull();
    }

    [Fact]
    public void ids_are_de_duplicated_in_first_seen_order()
        => PostMetricsIds.Parse("[\"b\",\"a\",\"b\"]").Should().Equal("b", "a");
}
