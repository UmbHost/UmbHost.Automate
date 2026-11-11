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

    /// <summary>Parses the setting. A blank value (or <c>null</c>) is no thread; an invalid one is an error message.</summary>
    public static (IReadOnlyList<string> Parts, string? Error) Parse(string? json)
    {
        // "null" is what Automate's `json` binding filter makes of a missing value: no thread, like a blank setting.
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
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

    /// <summary>Prefix of the machine-readable last line of a thread failure message.</summary>
    public const string TailPrefix = "UMBSOCIAL-THREAD ";

    /// <summary>Whether an HTTP status leaves it unknown if the request was applied (a 5xx), as opposed to a definite rejection.</summary>
    public static bool IsAmbiguous(System.Net.HttpStatusCode status) => (int)status >= 500;

    /// <summary>
    /// The terminal failure for a part that could not be posted after earlier parts were. Terminal
    /// (not retried) because a retry would post the earlier parts again. The message is a human
    /// sentence followed by one machine line, <c>UMBSOCIAL-THREAD {json}</c>, with the failed part
    /// (1-based), the part count, the https URLs already posted and whether the failed part may be live.
    /// </summary>
    /// <param name="partNumber">1-based number of the part that failed (the main text is part 1).</param>
    /// <param name="postedUrls">URLs of the parts already posted.</param>
    /// <param name="failedPartUnknown">True when the network did not confirm the outcome, so the failed part may also be published.</param>
    public static ActionResult PartFailed(
        string network, int partNumber, int totalParts, IReadOnlyList<string> postedUrls, string detail, bool failedPartUnknown)
    {
        var outcome = failedPartUnknown
            ? $"part {partNumber} of {totalParts} may also have been published: the network did not confirm the outcome ({detail}). Check the account before re-posting it."
            : $"part {partNumber} of {totalParts} could not be posted ({detail}).";
        var message =
            $"{network}: {outcome} Parts 1-{partNumber - 1} are already published: {string.Join(", ", postedUrls)}. " +
            "The thread was not retried, to avoid posting those parts twice.\n" +
            TailPrefix + JsonSerializer.Serialize(new
            {
                failedPart = partNumber,
                total = totalParts,
                posted = postedUrls.Where(u => u.StartsWith("https://", StringComparison.Ordinal)).ToArray(),
                failedPartUnknown,
            });
        return ActionResult.Failed(new InvalidOperationException(message), StepRunErrorCategory.ConfigurationError);
    }

    /// <summary>Reads the machine line from a failure message, or null when there is none.</summary>
    public static ThreadFailureTail? ParseTail(string message)
    {
        var line = message.Split('\n').LastOrDefault(l => l.StartsWith(TailPrefix, StringComparison.Ordinal));
        return line is null ? null : JsonSerializer.Deserialize<ThreadFailureTail>(line[TailPrefix.Length..]);
    }
}

/// <summary>The machine-readable tail of a thread failure.</summary>
internal sealed record ThreadFailureTail(
    [property: System.Text.Json.Serialization.JsonPropertyName("failedPart")] int FailedPart,
    [property: System.Text.Json.Serialization.JsonPropertyName("total")] int Total,
    [property: System.Text.Json.Serialization.JsonPropertyName("posted")] string[] Posted,
    [property: System.Text.Json.Serialization.JsonPropertyName("failedPartUnknown")] bool FailedPartUnknown);
