using Umbraco.Automate.Core.Connections;

namespace UmbHost.Automate.Social;

/// <summary>Adds a warning to a Test connection result without hiding what the check found.</summary>
internal static class ConnectionWarning
{
    /// <summary>
    /// A success becomes a warning carrying both messages; a warning gains the sentence; a failure, or no warning, is left as it is
    /// (the failure is the thing to fix first).
    /// </summary>
    public static ConnectionValidationResult Add(ConnectionValidationResult result, string? warning)
        => warning is null || result.Status == ConnectionValidationStatus.Failure
            ? result
            : ConnectionValidationResult.Warning(
                string.IsNullOrWhiteSpace(result.Message) ? warning : $"{result.Message}\n{warning}",
                result.Details.Count > 0 ? result.Details : null);
}
