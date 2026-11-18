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
    internal static int SizeOf(JsonObject message) =>
        Encoding.UTF8.GetByteCount(message.ToJsonString(WireOptions));

    /// <summary>
    /// Makes TextBlock show the text as typed. TextBlock renders a Markdown subset, so the Markdown
    /// characters are backslash-escaped, as is a line-leading list marker (<c>-</c>, <c>+</c>,
    /// <c>1.</c>). HTML is not rendered by TextBlock and is left alone. Kept in one place so the
    /// rule can change if a live check against Teams shows a different need.
    /// </summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var lineStart = true;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (lineStart)
            {
                if ((c == '-' || c == '+') && i + 1 < text.Length && text[i + 1] == ' ')
                {
                    sb.Append(Backslash).Append(c);
                    lineStart = false;
                    continue;
                }

                var digits = 0;
                while (i + digits < text.Length && char.IsAsciiDigit(text[i + digits]))
                {
                    digits++;
                }

                if (digits > 0 && i + digits + 1 < text.Length && text[i + digits] == '.' && text[i + digits + 1] == ' ')
                {
                    sb.Append(text, i, digits).Append(Backslash).Append('.');
                    i += digits;
                    lineStart = false;
                    continue;
                }
            }

            if (c is Backslash or '*' or '_' or '#' or '[' or ']' or '(' or ')' or '~' or '`')
            {
                sb.Append(Backslash);
            }

            sb.Append(c);
            lineStart = c == '\n';
        }

        return sb.ToString();
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
                if (!char.IsSurrogate(c) && Inner.WillEncode(c))
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
