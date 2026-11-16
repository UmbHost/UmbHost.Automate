using Microsoft.Extensions.Configuration;

namespace UmbHost.Automate.Social;

/// <summary>
/// Reads a provider's insights switch (<c>Umbraco:Automate:Providers:&lt;Provider&gt;:Insights</c>), which stays off until the network
/// has approved the app for the permissions its metrics need.
/// </summary>
internal static class InsightsSwitch
{
    /// <summary>On only for a case-insensitive <c>true</c>. Unset, <c>false</c> or anything malformed is off, and never throws.</summary>
    public static bool IsOn(IConfiguration configuration, string key)
        => string.Equals(configuration[key]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
