using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UmbHost.Automate.Teams.Cards;

/// <summary>
/// Builds the Adaptive Card message a Teams Workflows webhook accepts. Pure: no HTTP, no I/O.
/// </summary>
internal static class TeamsCard
{
    /// <summary>Teams' "about 28 KB" card limit, kept under.</summary>
    internal const int MaxBytes = 28_000;

    private const char Backslash = (char)92;

    /// <summary>
    /// Raw UTF-8 rather than \uXXXX escapes: an emoji is 4 bytes as UTF-8 but 12 as an escaped
    /// surrogate pair, and the size check must measure what is sent. The send step must serialise
    /// with these same options.
    /// </summary>
    internal static JsonSerializerOptions WireOptions { get; } = new()
    {
        Encoder = new RawUnicodeEncoder(),
    };

    internal static JsonObject Message(
        string text,
        string? imageUrl,
        string? imageAltText,
        string? linkUrl,
        string? linkTitle,
        string? title = null)
    {
        var body = new JsonArray();

        if (!string.IsNullOrEmpty(title))
        {
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = title,
                ["weight"] = "Bolder",
                ["size"] = "Medium",
                ["wrap"] = true,
            });
        }

        body.Add(new JsonObject
        {
            ["type"] = "TextBlock",
            ["text"] = Escape(text),
            ["wrap"] = true,
        });

        if (!string.IsNullOrEmpty(imageUrl))
        {
            var image = new JsonObject { ["type"] = "Image", ["url"] = imageUrl };
            if (!string.IsNullOrEmpty(imageAltText))
            {
                image["altText"] = imageAltText;
            }

            body.Add(image);
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };

        if (!string.IsNullOrEmpty(linkUrl))
        {
            card["actions"] = new JsonArray(new JsonObject
            {
                ["type"] = "Action.OpenUrl",
                ["title"] = string.IsNullOrEmpty(linkTitle) ? "Open link" : linkTitle,
                ["url"] = linkUrl,
            });
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["contentUrl"] = null,
                ["content"] = card,
            }),
        };
    }

    /// <summary>The message's size as UTF-8 JSON, serialised with <see cref="WireOptions"/>.</summary>
    internal static int SizeOf(JsonObject message) => ToWireBytes(message).Length;

    /// <summary>The exact UTF-8 bytes to send; the size check measures these same bytes.</summary>
    internal static byte[] ToWireBytes(JsonObject message) =>
        Encoding.UTF8.GetBytes(message.ToJsonString(WireOptions));

    /// <summary>
    /// Makes TextBlock show the text as typed. In Teams a TextBlock renders only bold, italic,
    /// lists and <c>[text](url)</c> links, so only what can start those is backslash-escaped:
    /// <c>\ * _ [ ]</c>, and a line-leading list marker (<c>-</c>, <c>+</c>, <c>*</c>, <c>1.</c>)
    /// after up to three spaces. Anything else (<c>#</c>, <c>(</c>, <c>~</c>, backticks, HTML) shows
    /// as typed without help. An http or https address typed in the text is left exactly as typed,
    /// so links such as <c>…/page#part</c> or <c>…/my_page</c> still work. Kept in one place so the
    /// rule can change if a live check against Teams shows a different need.
    /// </summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var lineStart = true;

        for (var i = 0; i < text.Length; i++)
        {
            if (lineStart)
            {
                lineStart = false;

                var spaces = 0;
                while (spaces < 4 && i + spaces < text.Length && text[i + spaces] == ' ')
                {
                    spaces++;
                }

                if (spaces <= 3 && TryListMarker(text, i + spaces, out var markerLength))
                {
                    // The spaces and any digits as typed, then the escaped marker character.
                    sb.Append(text, i, spaces + markerLength - 1).Append(Backslash).Append(text[i + spaces + markerLength - 1]);
                    i += spaces + markerLength - 1;
                    continue;
                }
            }

            var urlLength = UrlLength(text, i);
            if (urlLength > 0)
            {
                sb.Append(text, i, urlLength);
                i += urlLength - 1;
                continue;
            }

            var c = text[i];
            if (c is Backslash or '*' or '_' or '[' or ']')
            {
                sb.Append(Backslash);
            }

            sb.Append(c);
            lineStart = c == '\n';
        }

        return sb.ToString();
    }

    /// <summary>A list marker at <paramref name="at"/>: <c>-</c>, <c>+</c> or <c>*</c>, or digits and <c>.</c>, then a space.</summary>
    private static bool TryListMarker(string text, int at, out int length)
    {
        length = 0;
        if (at >= text.Length)
        {
            return false;
        }

        if (text[at] is '-' or '+' or '*')
        {
            length = 1;
        }
        else
        {
            var digits = 0;
            while (at + digits < text.Length && char.IsAsciiDigit(text[at + digits]))
            {
                digits++;
            }

            if (digits == 0 || at + digits >= text.Length || text[at + digits] != '.')
            {
                return false;
            }

            length = digits + 1;
        }

        return at + length < text.Length && text[at + length] == ' ';
    }

    /// <summary>The length of an http or https address starting at <paramref name="at"/> (up to the next whitespace), or 0.</summary>
    private static int UrlLength(string text, int at)
    {
        var rest = text.AsSpan(at);
        int scheme;
        if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = 8;
        }
        else if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = 7;
        }
        else
        {
            return 0;
        }

        var length = scheme;
        while (length < rest.Length && !char.IsWhiteSpace(rest[length]))
        {
            length++;
        }

        return length > scheme ? length : 0;
    }

    /// <summary>
    /// Writes every character raw except the ones JSON requires escaped. The built-in relaxed
    /// encoder still escapes characters outside the Basic Multilingual Plane (emoji) as 12-byte
    /// surrogate pairs; core has no option to stop that, so this small encoder lets them through.
    /// </summary>
    private sealed class RawUnicodeEncoder : JavaScriptEncoder
    {
        private static readonly JavaScriptEncoder Inner = UnsafeRelaxedJsonEscaping;

        public override int MaxOutputCharactersPerInputCharacter => Inner.MaxOutputCharactersPerInputCharacter;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar < 0x10000 && Inner.WillEncode(unicodeScalar);

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < textLength && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                // An unpaired surrogate is flagged so the inner encoder writes U+FFFD for it.
                if (char.IsSurrogate(c) || Inner.WillEncode(c))
                {
                    return i;
                }
            }

            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten) =>
            Inner.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
    }
}
