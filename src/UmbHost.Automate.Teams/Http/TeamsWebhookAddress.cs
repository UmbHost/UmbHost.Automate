using System.Web;

namespace UmbHost.Automate.Teams.Http;

/// <summary>
/// Rules for a Teams Workflows webhook address. The address is a secret (its <c>sig</c> query
/// parameter authorises a post), so nothing here ever puts the path or query into an error or a
/// host string. The allow-list is fixed in code: a site cannot widen it.
/// </summary>
internal static class TeamsWebhookAddress
{
    private const string NotValid = "(not a valid address)";

    internal static IReadOnlyList<string> AllowedHostSuffixes { get; } =
        ["logic.azure.com", "api.powerplatform.com"];

    internal static bool TryParse(string? value, out Uri? address, out string? error)
    {
        address = null;
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            error = "The Teams webhook address is empty.";
            return false;
        }

        if (text.StartsWith("$Umbraco:", StringComparison.OrdinalIgnoreCase))
        {
            error = "The Teams webhook address is a secret reference that did not resolve.";
            return false;
        }

        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "The Teams webhook address is not a valid address.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "The Teams webhook address must use https.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "The Teams webhook address must not contain a user name or password.";
            return false;
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            error = "The Teams webhook address must use a host name, not an IP address.";
            return false;
        }

        if (uri.Port != 443)
        {
            error = $"The Teams webhook address on {uri.Host} must use port 443.";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (!AllowedHostSuffixes.Any(s => host.EndsWith("." + s, StringComparison.Ordinal)))
        {
            error = $"{uri.Host} is not a Microsoft Teams Workflows host.";
            return false;
        }

        if (!uri.AbsolutePath.Contains("/workflows/", StringComparison.OrdinalIgnoreCase))
        {
            error = $"The address on {uri.Host} is not a Workflows webhook address.";
            return false;
        }

        if (string.IsNullOrEmpty(HttpUtility.ParseQueryString(uri.Query)["sig"]))
        {
            error = $"The address on {uri.Host} is missing its signature.";
            return false;
        }

        address = uri;
        error = null;
        return true;
    }

    /// <summary>The host only, or a fixed phrase. The only part of an address an error may show.</summary>
    internal static string HostOf(string? value)
    {
        var text = value?.Trim();
        return !string.IsNullOrEmpty(text) && !text.Any(char.IsWhiteSpace)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : NotValid;
    }
}
