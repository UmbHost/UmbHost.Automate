using System.Text;
using System.Text.RegularExpressions;

namespace UmbHost.Automate.Bluesky.Actions;

/// <summary>
/// A link inside post text, located by UTF-8 byte offsets (start inclusive, end exclusive).
/// </summary>
internal sealed record LinkFacet(int ByteStart, int ByteEnd, string Uri);

/// <summary>
/// Finds the URLs in post text. Bluesky doesn't detect links itself: a URL is only clickable if the
/// post carries a <c>app.bsky.richtext.facet#link</c> facet covering its UTF-8 byte range.
/// </summary>
internal static partial class LinkFacets
{
    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '\'', '"'];

    public static IReadOnlyList<LinkFacet> Find(string text)
    {
        var facets = new List<LinkFacet>();
        foreach (Match match in UrlPattern().Matches(text))
        {
            var uri = match.Value.TrimEnd(TrailingPunctuation);
            var byteStart = Encoding.UTF8.GetByteCount(text.AsSpan(0, match.Index));
            facets.Add(new LinkFacet(byteStart, byteStart + Encoding.UTF8.GetByteCount(uri), uri));
        }

        return facets;
    }
}
