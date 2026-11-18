using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;

namespace UmbHost.Automate.Social;

/// <summary>
/// Turns a network's error answer into the plain sentence a person reads (the step's error, which UmbSocial shows as the network's
/// details), and writes the whole answer to the step's own log for support (Automate keeps it with the step run, shown in the run's
/// detail, after its sanitiser has run).
/// </summary>
/// <remarks>
/// Neither Automate nor Umbraco reads a network's error body, so this small reader lives here. It knows each network's documented shape
/// (checked 2026-11-18):
/// <list type="bullet">
/// <item>Graph (Facebook, Instagram, Threads), https://developers.facebook.com/docs/graph-api/guides/error-handling:
/// <c>{"error":{"message","code","error_subcode","error_user_title","error_user_msg","fbtrace_id"}}</c>; <c>error_user_msg</c> is
/// "the message to display to the user".</item>
/// <item>Google (Business Profile), https://google.aip.dev/193: <c>{"error":{"code","message","status","details"}}</c>.</item>
/// <item>Bluesky (XRPC), https://atproto.com/specs/xrpc: <c>{"error","message"}</c>; <c>message</c> is "appropriate for display to
/// humans".</item>
/// <item>Mastodon, https://docs.joinmastodon.org/methods/media/: <c>{"error"}</c> (and <c>error_description</c> from OAuth).</item>
/// <item>X v2, https://docs.x.com/x-api/fundamentals/response-codes-and-errors: <c>{"title","detail","type"}</c>; older answers
/// <c>{"errors":[{"message"}]}</c>.</item>
/// <item>LinkedIn, https://learn.microsoft.com/en-us/linkedin/shared/api-guide/concepts/error-handling: <c>{"message","status",
/// "serviceErrorCode"}</c>.</item>
/// <item>Pinterest, https://github.com/pinterest/api-description (v5 openapi.yaml, Pinterest.Lib.Error): <c>{"code","message"}</c>.</item>
/// </list>
/// Anything else (an HTML page, an empty body) gives "&lt;network&gt; returned &lt;code&gt;." and the body stays in the log only.
/// Long runs of token-like characters are masked in both the sentence and the log, so a network that echoes an access token
/// ("Malformed access token …") never stores it; the request URL (which can carry a token) is never written.
/// </remarks>
internal static partial class NetworkError
{
    /// <summary>The longest sentence kept as the step's error.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>The most of the answer written to the step log (Automate's sanitiser cuts there too).</summary>
    public const int MaxLoggedBodyLength = 4_000;

    /// <summary>What a masked token-like run reads as.</summary>
    public const string Masked = "[redacted]";

    /// <summary>
    /// The sentence for a failed answer: the network's own words where its body has them, else "&lt;network&gt; [stage] returned
    /// &lt;code&gt;.". One line, at most <see cref="MaxMessageLength"/> characters, token-like runs masked.
    /// </summary>
    public static string Plain(string network, string? stage, HttpStatusCode status, string? body)
        => Words(body) is { } words ? words : $"{Prefix(network, stage)} returned {(int)status}.";

    /// <summary>Writes the whole answer to the step's log: "&lt;network&gt; [stage] returned &lt;code&gt;: &lt;body&gt;", masked and cut.</summary>
    public static void Log(ActionContext? context, string network, string? stage, HttpStatusCode status, string? body)
    {
        if (context is null)
        {
            return;
        }

        var text = Mask(body ?? string.Empty);
        if (text.Length > MaxLoggedBodyLength)
        {
            text = text[..MaxLoggedBodyLength] + "…";
        }

        context.LogError($"{Prefix(network, stage)} returned {(int)status}: {text}");
    }

    /// <summary>The Graph error code (<c>error.code</c>) when the body is a Graph error; null otherwise.</summary>
    public static int? GraphCode(string? body)
    {
        if (Parse(body) is not { } root || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number)
            ? number
            : null;
    }

    /// <summary>Masks every run of 32 or more token-like characters (access tokens, JWTs, signed values).</summary>
    public static string Mask(string text) => TokenLike().Replace(text, Masked);

    /// <summary>The network's own words from its error body, made safe to show; null when it has none.</summary>
    internal static string? Words(string? body)
    {
        if (Parse(body) is not { } root)
        {
            return null;
        }

        var words = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "error_user_msg") ?? Text(error, "message")
            : Text(root, "detail")
              ?? Text(root, "message")
              ?? Text(root, "error_description")
              ?? FirstError(root)
              ?? Text(root, "title")
              ?? Text(root, "error");

        return words is null ? null : Clean(words);
    }

    private static string Prefix(string network, string? stage) => string.IsNullOrWhiteSpace(stage) ? network : $"{network} {stage}";

    private static JsonElement? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? FirstError(JsonElement root)
        => root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
           && errors[0].ValueKind == JsonValueKind.Object
            ? Text(errors[0], "message") ?? Text(errors[0], "detail")
            : null;

    /// <summary>One line (so it can't pass for a machine line such as a thread's tail), masked and cut.</summary>
    private static string? Clean(string words)
    {
        var line = new StringBuilder(words.Length);
        foreach (var c in words)
        {
            line.Append(char.IsControl(c) ? ' ' : c);
        }

        var text = Mask(Spaces().Replace(line.ToString(), " ").Trim());
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 1)] + "…";
    }

    [GeneratedRegex(@"[A-Za-z0-9\-_.~+/=|]{32,}")]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
