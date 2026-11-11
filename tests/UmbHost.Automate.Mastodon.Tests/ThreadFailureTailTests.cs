using FluentAssertions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Mastodon.Tests;

// ThreadParts is compiled into every provider from src/Shared; the format is pinned once here.
public class ThreadFailureTailTests
{
    [Fact]
    public void The_failure_message_ends_in_a_compact_machine_line_with_only_https_urls()
    {
        var result = ThreadParts.PartFailed(
            "Net", 3, 4, ["https://a.example/1", "http://not-https.example/2"], "boom", failedPartUnknown: true);

        var lines = result.Exception!.Message.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("Net: part 3 of 4 may also have been published");
        lines[1].Should().Be("""UMBSOCIAL-THREAD {"failedPart":3,"total":4,"posted":["https://a.example/1"],"failedPartUnknown":true}""");
    }

    [Fact]
    public void The_tail_round_trips()
    {
        var result = ThreadParts.PartFailed("Net", 2, 5, ["https://a.example/1"], "nope", failedPartUnknown: false);

        var tail = ThreadParts.ParseTail(result.Exception!.Message);

        tail.Should().NotBeNull();
        tail!.FailedPart.Should().Be(2);
        tail.Total.Should().Be(5);
        tail.Posted.Should().Equal("https://a.example/1");
        tail.FailedPartUnknown.Should().BeFalse();
    }

    [Fact]
    public void A_message_without_a_tail_parses_to_null()
        => ThreadParts.ParseTail("just words").Should().BeNull();
}

// Automate's `json` binding filter turns a missing value into the string "null".
public class ThreadPartsBindingTests
{
    [Theory]
    [InlineData("null")]
    [InlineData(" null ")]
    [InlineData("[]")]
    public void A_null_thread_binding_is_no_thread(string json)
    {
        var (parts, error) = ThreadParts.Parse(json);

        error.Should().BeNull();
        parts.Should().BeEmpty();
    }
}
