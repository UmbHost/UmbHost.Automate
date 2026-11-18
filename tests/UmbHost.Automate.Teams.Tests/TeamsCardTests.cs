using System.Text.Json.Nodes;
using FluentAssertions;
using UmbHost.Automate.Teams.Cards;

namespace UmbHost.Automate.Teams.Tests;

public class TeamsCardTests
{
    private static JsonObject Content(JsonObject message) =>
        message["attachments"]![0]!["content"]!.AsObject();

    private static JsonArray Body(JsonObject message) => Content(message)["body"]!.AsArray();

    [Fact]
    public void wraps_the_card_in_a_message_envelope()
    {
        var message = TeamsCard.Message("hi", null, null, null, null);

        message["type"]!.GetValue<string>().Should().Be("message");
        var attachment = message["attachments"]![0]!;
        attachment["contentType"]!.GetValue<string>().Should().Be("application/vnd.microsoft.card.adaptive");
        attachment["contentUrl"].Should().BeNull();
        var content = Content(message);
        content["type"]!.GetValue<string>().Should().Be("AdaptiveCard");
        content["version"]!.GetValue<string>().Should().Be("1.4");
        content["$schema"]!.GetValue<string>().Should().Be("http://adaptivecards.io/schemas/adaptive-card.json");
    }

    [Fact]
    public void text_only()
    {
        var message = TeamsCard.Message("Hello world", null, null, null, null);

        var body = Body(message);
        body.Should().HaveCount(1);
        body[0]!["type"]!.GetValue<string>().Should().Be("TextBlock");
        body[0]!["wrap"]!.GetValue<bool>().Should().BeTrue();
        body[0]!["text"]!.GetValue<string>().Should().Be(TeamsCard.Escape("Hello world"));
        Content(message).ContainsKey("actions").Should().BeFalse();
    }

    [Fact]
    public void image_with_alt_text()
    {
        var message = TeamsCard.Message("x", "https://example.com/a.png", "A cat", null, null);

        var image = Body(message).Single(e => e!["type"]!.GetValue<string>() == "Image")!;
        image["url"]!.GetValue<string>().Should().Be("https://example.com/a.png");
        image["altText"]!.GetValue<string>().Should().Be("A cat");
    }

    [Fact]
    public void link_becomes_a_button()
    {
        var withTitle = TeamsCard.Message("x", null, null, "https://example.com/p", "Read more");
        var actions = Content(withTitle)["actions"]!.AsArray();
        actions.Should().HaveCount(1);
        actions[0]!["type"]!.GetValue<string>().Should().Be("Action.OpenUrl");
        actions[0]!["url"]!.GetValue<string>().Should().Be("https://example.com/p");
        actions[0]!["title"]!.GetValue<string>().Should().Be("Read more");

        var without = TeamsCard.Message("x", null, null, "https://example.com/p", null);
        Content(without)["actions"]![0]!["title"]!.GetValue<string>().Should().Be("Open link");
    }

    [Fact]
    public void title_is_a_bold_heading()
    {
        var message = TeamsCard.Message("body", null, null, null, null, title: "UmbSocial test");

        var first = Body(message)[0]!;
        first["type"]!.GetValue<string>().Should().Be("TextBlock");
        first["weight"]!.GetValue<string>().Should().Be("Bolder");
        first["size"]!.GetValue<string>().Should().Be("Medium");
        first["text"]!.GetValue<string>().Should().Be("UmbSocial test");
    }

    [Theory]
    [InlineData("*bold*", @"\*bold\*")]
    [InlineData("_x_", @"\_x\_")]
    [InlineData("# heading", @"\# heading")]
    [InlineData("[a](https://b)", @"\[a\]\(https://b\)")]
    [InlineData("<b>hi</b>", "<b>hi</b>")]
    [InlineData("1. item", @"1\. item")]
    [InlineData("- item", @"\- item")]
    [InlineData("+ item", @"\+ item")]
    [InlineData("a ~b~ `c` \\d", @"a \~b\~ \`c\` \\d")]
    [InlineData("line one\n- two\n2. three", "line one\n\\- two\n2\\. three")]
    public void text_shows_as_written(string input, string expected)
    {
        TeamsCard.Escape(input).Should().Be(expected);
        Body(TeamsCard.Message(input, null, null, null, null))[0]!["text"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void socials_largest_post_fits_the_card_limit()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 4000));
        var alt = new string('a', 1000);
        var link = "https://example.com/" + new string('p', 1980);
        var linkTitle = new string('t', 300);

        var message = TeamsCard.Message(text, "https://example.com/i.png", alt, link, linkTitle, title: "UmbSocial test");

        TeamsCard.SizeOf(message).Should().BeLessThan(TeamsCard.MaxBytes);
    }

    public static TheoryData<string> WireInputs => new()
    {
        "plain " + char.ConvertFromUtf32(0x1F600) + " emoji",
        "quote \" and slash " + (char)92 + " end",
        "ctl " + (char)0 + (char)0x1f + (char)0x7f + (char)0x2028 + " end",
        "x" + (char)0xD83D,
        (char)0xD83D + "x",
        "a" + (char)0xDE00 + "b",
    };

    [Theory]
    [MemberData(nameof(WireInputs))]
    public void the_wire_bytes_parse_back_to_the_text(string input)
    {
        var bytes = TeamsCard.ToWireBytes(TeamsCard.Message(input, null, null, null, null));

        var parsed = JsonNode.Parse(bytes)!.AsObject();
        var text = parsed["attachments"]![0]!["content"]!["body"]![0]!["text"]!.GetValue<string>();

        var expected = TeamsCard.Escape(input);
        for (var i = 0; i < expected.Length; i++)
        {
            var lone = char.IsSurrogate(expected[i])
                && !(char.IsHighSurrogate(expected[i]) && i + 1 < expected.Length && char.IsLowSurrogate(expected[i + 1]))
                && !(char.IsLowSurrogate(expected[i]) && i > 0 && char.IsHighSurrogate(expected[i - 1]));
            if (lone)
            {
                expected = expected.Remove(i, 1).Insert(i, ((char)0xFFFD).ToString());
            }
        }

        text.Should().Be(expected);
    }

    [Fact]
    public void emoji_go_out_as_raw_utf8()
    {
        var bytes = TeamsCard.ToWireBytes(TeamsCard.Message(char.ConvertFromUtf32(0x1F600), null, null, null, null));

        bytes.AsSpan().IndexOf(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }).Should().BeGreaterThan(-1);
        System.Text.Encoding.UTF8.GetString(bytes).Should().NotContain("uD83D");
    }

    [Fact]
    public void size_is_the_length_of_the_wire_bytes()
    {
        var m = TeamsCard.Message(char.ConvertFromUtf32(0x1F600) + " hi", null, null, "https://e.com", null);
        TeamsCard.SizeOf(m).Should().Be(TeamsCard.ToWireBytes(m).Length);
    }
}
