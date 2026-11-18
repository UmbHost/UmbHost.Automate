using System.Text.Json.Nodes;

namespace UmbHost.Automate.Threads.Tests;

/// <summary>
/// A reply the real network gave, captured and redacted into <c>Fixtures/Live/&lt;name&gt;.json</c>. Where and when each was captured,
/// and what was redacted, is in <c>Fixtures/Live/README.md</c>.
/// </summary>
internal static class LiveFixture
{
    /// <summary>The reply's text as stored, without the file's final newline (error answers are byte for byte what the network sent).</summary>
    public static string Text(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Live", name + ".json")).TrimEnd('\n');

    /// <summary>The reply as a fresh JSON object, for a test that needs to change one field (an id, a count).</summary>
    public static JsonObject Object(string name) => JsonNode.Parse(Text(name))!.AsObject();
}
