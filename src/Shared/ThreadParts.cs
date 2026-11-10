using System.Text.Json;
using Umbraco.Automate.Core.Actions;

namespace UmbHost.Automate.Social;

/// <summary>
/// The shared shape of a thread, so every provider reads the same setting.
/// </summary>
/// <remarks>
/// <para>
/// The <c>ThreadParts</c> setting is a JSON array of strings: the parts that follow the main text, in
/// order, e.g. <c>["Second part", "Third part"]</c>. Each is posted as a reply to the part before it.
/// Blank entries are not allowed.
/// </para>
/// <para>
/// A thread is published only when every part is. If a later part fails the step fails with a
/// terminal error that names the part (1-based) and lists the URLs already posted. It is never
/// retried, because the earlier parts would post a second time.
/// </para>
/// </remarks>
internal static class ThreadParts
{
    /// <summary>The most extra parts a thread may have after the main text.</summary>
    public const int MaxExtraParts = 10;

    /// <summary>Parses the setting. A blank value is no thread; an invalid one is an error message.</summary>
    public static (IReadOnlyList<string> Parts, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ([], null);
        }

        string[]? parts;
        try
        {
            parts = JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            parts = null;
        }

        if (parts is null)
        {
            return ([], "Thread parts must be a JSON array of strings, e.g. [\"Second part\",\"Third part\"].");
        }

        if (parts.Length > MaxExtraParts)
        {
            return ([], $"A thread can have at most {MaxExtraParts} parts after the first.");
        }

        if (parts.Any(string.IsNullOrWhiteSpace))
        {
            return ([], "Thread parts can't be blank.");
        }

        return (parts, null);
    }

    /// <summary>
    /// The terminal failure for a part that could not be posted after earlier parts were. Terminal
    /// (not retried) because a retry would post the earlier parts again.
    /// </summary>
    /// <param name="partNumber">1-based number of the part that failed (the main text is part 1).</param>
    /// <param name="postedUrls">URLs of the parts already posted.</param>
    public static ActionResult PartFailed(string network, int partNumber, int totalParts, IReadOnlyList<string> postedUrls, string detail)
        => ActionResult.Failed(
            new InvalidOperationException(
                $"{network}: part {partNumber} of {totalParts} could not be posted ({detail}). " +
                $"Parts 1-{partNumber - 1} are already published: {string.Join(", ", postedUrls)}. " +
                "The thread was not retried, to avoid posting those parts twice."),
            StepRunErrorCategory.ConfigurationError);
}
