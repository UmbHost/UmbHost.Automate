using FluentAssertions;
using UmbHost.Automate.Bluesky.Actions;

namespace UmbHost.Automate.Bluesky.Tests;

public class LinkFacetsTests
{
    [Fact]
    public void Offsets_are_utf8_bytes_not_chars()
    {
        // "🚀 " is 4 + 1 bytes in UTF-8 but 3 UTF-16 chars.
        var facets = LinkFacets.Find("🚀 https://a.io");

        facets.Should().ContainSingle();
        facets[0].ByteStart.Should().Be(5);
        facets[0].ByteEnd.Should().Be(5 + "https://a.io".Length);
    }

    [Fact]
    public void Trailing_punctuation_is_not_part_of_the_link()
    {
        var facets = LinkFacets.Find("See https://umbhost.net/blog.");

        facets.Single().Uri.Should().Be("https://umbhost.net/blog");
    }

    [Fact]
    public void Finds_every_link()
    {
        LinkFacets.Find("https://a.io and http://b.io/x?y=1").Select(f => f.Uri)
            .Should().Equal("https://a.io", "http://b.io/x?y=1");
    }

    [Fact]
    public void No_links_no_facets() => LinkFacets.Find("plain text").Should().BeEmpty();
}
