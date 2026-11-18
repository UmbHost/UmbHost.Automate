using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Connection;

/// <summary>
/// The Graph API version a Meta connection calls (<c>https://graph.facebook.com/{version}/</c>): its format, the connector's current
/// default, how far it is behind that default, and Meta's answer when it is deprecated.
/// </summary>
/// <remarks>
/// Checked 2026-11-18. https://developers.facebook.com/docs/graph-api/guides/versioning: "Each version is guaranteed to operate for at
/// least two years", and "once a version is no longer usable, any calls made to it will be defaulted to the next oldest, usable version".
/// So Graph usually answers an expired version rather than refusing it; the <c>facebook-api-version</c> response header names the
/// version that actually answered (https://developers.facebook.com/docs/graph-api/guides/debugging/), which Test connection compares.
/// Where Meta does refuse (its Marketing API during a deprecation), the error is
/// "(#2635) You are calling a deprecated version of the Ads API. Please update to the latest version: vXXX"
/// (https://developers.facebook.com/blog/post/2024/04/16/upcoming-change-in-marketing-api-version-deprecation); code 2635 is
/// <b>not</b> documented for Pages or Instagram calls, so it is matched defensively here.
/// Release dates are from https://developers.facebook.com/docs/graph-api/changelog/versions, except v27.0 (4 November 2026), which
/// that page didn't list yet when checked and is from Meta's release coverage. Add each new default's date here when it moves.
/// </remarks>
internal static partial class MetaGraphVersion
{
    /// <summary>
    /// The version the connector calls when a connection leaves Graph API Version empty. Moved forward with each release, so a
    /// connection left empty follows it; one that names a version keeps it.
    /// </summary>
    public const string Default = "v27.0";

    /// <summary>A version released more than this many months before <see cref="Default"/> gets Test connection's warning.</summary>
    public const int WarnAfterMonthsBehindDefault = 18;

    /// <summary>The message for a value that isn't a Graph version.</summary>
    public const string FormatMessage = "Use the form vNN.N, e.g. " + Default + ".";

    /// <summary>The response header naming the version that answered.</summary>
    public const string ServedHeader = "facebook-api-version";

    private static readonly Dictionary<int, DateOnly> Released = new()
    {
        [20] = new(2024, 5, 21),
        [21] = new(2024, 10, 2),
        [22] = new(2025, 1, 21),
        [23] = new(2025, 5, 29),
        [24] = new(2025, 10, 8),
        [25] = new(2026, 2, 18),
        [26] = new(2026, 7, 29),
        [27] = new(2026, 11, 4),
    };

    /// <summary>The version to call: the connection's, or <see cref="Default"/> when it is empty.</summary>
    public static string Effective(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? Default : configured.Trim().Trim('/');

    /// <summary>Why <paramref name="value"/> can't be used, or null when it can (empty means the default).</summary>
    public static string? Problem(string? value)
        => string.IsNullOrWhiteSpace(value) || Parse(value.Trim().Trim('/')) is not null ? null : FormatMessage;

    /// <summary>
    /// The Test connection warning for a version released more than <see cref="WarnAfterMonthsBehindDefault"/> months before the
    /// connector's default, or null. A version older than every dated one counts as that old.
    /// </summary>
    public static string? StaleWarning(string? configured)
    {
        var version = Effective(configured);
        if (Parse(version) is not { } parsed || Parse(Default) is not { } current || parsed.Major >= current.Major)
        {
            return null;
        }

        var defaultReleased = Released[current.Major];
        var stale = Released.TryGetValue(parsed.Major, out var released)
            ? released < defaultReleased.AddMonths(-WarnAfterMonthsBehindDefault)
            : parsed.Major < Released.Keys.Min();
        return stale
            ? $"Graph API Version {version} is more than {WarnAfterMonthsBehindDefault} months older than the connector's default. " +
              $"Meta supports each version for about two years; move to a newer one (e.g. {Default})."
            : null;
    }

    /// <summary>
    /// The warning when Meta answered on a newer version than the one asked for (it has stopped serving that one), or null.
    /// </summary>
    public static string? ServedWarning(string requested, string? served)
    {
        if (string.IsNullOrWhiteSpace(served) || Parse(requested) is not { } asked || Parse(served.Trim()) is not { } answered
            || answered.CompareTo(asked) <= 0)
        {
            return null;
        }

        return $"Meta answered on {served.Trim()}, not {requested}: it no longer serves {requested} and moves its calls on. " +
               "Set a newer version in the connection's Graph API Version.";
    }

    /// <summary>
    /// Whether Graph's answer says the version is deprecated: error code 2635, or an error message about a deprecated version.
    /// </summary>
    public static bool IsDeprecatedAnswer(HttpStatusCode status, string? body)
    {
        if ((int)status is < 400 or > 499)
        {
            return false;
        }

        if (NetworkError.GraphCode(body) == 2635)
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(body ?? string.Empty);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                   && message.GetString()!.Contains("deprecated version", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The plain sentence for a deprecated version.</summary>
    public static string RefusalMessage(string version)
        => $"Meta no longer supports Graph API version {version}. Set a newer version in the connection's Graph API Version.";

    /// <summary>
    /// The step's failure when Graph refused the version (Validation: it needs the admin, not a retry), with the whole answer in the
    /// step's log; null for any other answer.
    /// </summary>
    public static ActionResult? Refused(ActionContext? context, string network, string? stage, string version, HttpStatusCode status, string body)
    {
        if (!IsDeprecatedAnswer(status, body))
        {
            return null;
        }

        NetworkError.Log(context, network, stage, status, body);
        return ActionResult.Failed(new InvalidOperationException(RefusalMessage(version)), StepRunErrorCategory.Validation);
    }

    /// <summary>The version a client made by <c>GraphClient.Create</c> calls (the first segment of its base address).</summary>
    public static string VersionOf(HttpClient client)
        => client.BaseAddress?.Segments is { Length: > 1 } segments ? segments[1].Trim('/') : Default;

    private static Version? Parse(string value)
    {
        var match = Format().Match(value);
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? new Version(major, minor)
            : null;
    }

    // ^v\d+\.\d+$ in ASCII: .NET's \d also matches other scripts' digits, and \z, unlike $, refuses a trailing newline.
    [GeneratedRegex(@"^v([0-9]{1,4})\.([0-9]{1,4})\z")]
    private static partial Regex Format();
}

/// <summary>
/// Checks a Graph API Version field with <see cref="MetaGraphVersion.Problem"/>. Automate runs a settings property's validation
/// attributes when it resolves the connection's settings (Test connection, and every step that uses the connection).
/// </summary>
/// <remarks>
/// Automate 17.5 has no public hook that rejects a connection's settings on save: <c>IConnectionSettingsSaveHandler</c> is internal, and
/// a cancelled <c>ConnectionSavingNotification</c> reaches the backoffice as a bare "Server Error". The backoffice's Test connection saves
/// first and then resolves the settings, so a bad value is reported there. Replace this with a save-time check when Automate offers one.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class MetaGraphVersionAttribute : ValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        => MetaGraphVersion.Problem(value as string) is { } problem
            ? new ValidationResult(problem, validationContext.MemberName is { } member ? [member] : null)
            : ValidationResult.Success;
}
