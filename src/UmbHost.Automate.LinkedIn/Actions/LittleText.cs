using System.Text;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Escapes post text for LinkedIn's "little" text format, which the Posts API <c>commentary</c>
/// field is parsed as. Reserved characters must be backslash-escaped even when they aren't part of
/// a mention or template, or LinkedIn truncates or misreads the text.
/// </summary>
/// <remarks>
/// A <c>#</c> directly followed by a letter or digit is left alone: that is the format's own
/// hashtag element, so hashtags typed in the text stay hashtags.
/// </remarks>
internal static class LittleText
{
    private const string Reserved = "|{}@[]()<>#\\*_~";

    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isHashtag = c == '#' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
            if (Reserved.Contains(c) && !isHashtag)
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
