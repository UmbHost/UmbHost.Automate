using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// The <c>LinkedIn-Version</c> header a connection sends: its format, the connector's current default, how old it is, and LinkedIn's
/// answer when it has been retired.
/// </summary>
/// <remarks>
/// From https://learn.microsoft.com/en-us/linkedin/marketing/versioning (checked 2026-11-18, page dated 2026-11-16): the header is
/// "format: YYYYMM"; "LinkedIn Marketing API Program publishes new versions monthly, and those versions are supported for a minimum of
/// one (1) year"; "the latest version is not applied by default". Its sunset notice ("202601 ... will be sunset on January 15, 2027")
/// shows the year in practice. A retired version is answered, per
/// https://learn.microsoft.com/en-us/linkedin/marketing/error-responses (Example 5, "Version header is deprecated"), with
/// <c>{"status":426,"code":"NONEXISTENT_VERSION","message":"Requested version yyyymmdd is not active"}</c>; a missing one (Example 4)
/// with 400 <c>VERSION_MISSING</c>.
/// </remarks>
internal static partial class LinkedInApiVersion
{
    /// <summary>
    /// The version the connector sends when a connection leaves API Version empty. Moved forward with each release, so a connection
    /// left empty follows it; one that names a version keeps it.
    /// </summary>
    public const string Default = "202611";

    /// <summary>Older than this many months, Test connection warns: LinkedIn supports a version for a year, so this leaves two.</summary>
    public const int WarnAfterMonths = 10;

    /// <summary>The message for a value that isn't a YYYYMM version.</summary>
    public const string FormatMessage = "Use the form YYYYMM, e.g. " + Default + ".";

    /// <summary>The version to send: the connection's, or <see cref="Default"/> when it is empty.</summary>
    public static string Effective(string? configured) => string.IsNullOrWhiteSpace(configured) ? Default : configured.Trim();

    /// <summary>
    /// Why <paramref name="value"/> can't be sent, or null when it can: empty (the default), or YYYYMM with a month 01-12 that isn't
    /// after <paramref name="now"/>'s month (UTC).
    /// </summary>
    public static string? Problem(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Parse(value.Trim()) is not { } month)
        {
            return FormatMessage;
        }

        return month > MonthIndex(now)
            ? $"API Version {value.Trim()} is in the future: LinkedIn only has versions up to this month. {FormatMessage}"
            : null;
    }

    /// <summary>How many whole months old <paramref name="version"/> is at <paramref name="now"/>; null when it isn't YYYYMM.</summary>
    public static int? AgeInMonths(string version, DateTimeOffset now)
        => Parse(version) is { } month ? MonthIndex(now) - month : null;

    /// <summary>
    /// The Test connection warning for a version more than <see cref="WarnAfterMonths"/> months old, or null. It suggests the
    /// connector's default while that is recent, else last month's version.
    /// </summary>
    public static string? StaleWarning(string? configured, DateTimeOffset now)
    {
        var version = Effective(configured);
        if (AgeInMonths(version, now) is not { } age || age <= WarnAfterMonths)
        {
            return null;
        }

        var suggestion = AgeInMonths(Default, now) is { } defaultAge && defaultAge <= WarnAfterMonths && defaultAge < age
            ? Default
            : now.UtcDateTime.AddMonths(-1).ToString("yyyyMM", CultureInfo.InvariantCulture);
        return $"API Version {version} is {age} months old. LinkedIn retires API versions about a year after release; " +
               $"move to a newer one (e.g. {suggestion}).";
    }

    /// <summary>
    /// Whether LinkedIn's answer says the version can't be used: 426 Upgrade Required, or a body whose <c>code</c> is
    /// <c>NONEXISTENT_VERSION</c> or <c>VERSION_MISSING</c>, or whose message says the requested version is not active.
    /// </summary>
    public static bool IsVersionRefusal(HttpStatusCode status, string? body)
    {
        if ((int)status == 426)
        {
            return true;
        }

        if ((int)status is < 400 or > 499 || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is "NONEXISTENT_VERSION" or "VERSION_MISSING")
            {
                return true;
            }

            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                   && NotActive().IsMatch(message.GetString()!);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The plain sentence for a refused version.</summary>
    public static string RefusalMessage(string version)
        => $"LinkedIn no longer supports API version {version}. Set a newer version in the connection's API Version.";

    /// <summary>
    /// The step's failure when LinkedIn refused the version (Validation: it needs the admin, not a retry), with the whole answer in the
    /// step's log; null for any other answer.
    /// </summary>
    public static ActionResult? Refused(ActionContext? context, string? stage, string version, HttpStatusCode status, string body)
    {
        if (!IsVersionRefusal(status, body))
        {
            return null;
        }

        NetworkError.Log(context, "LinkedIn", stage, status, body);
        return ActionResult.Failed(new InvalidOperationException(RefusalMessage(version)), StepRunErrorCategory.Validation);
    }

    private static int MonthIndex(DateTimeOffset now) => (now.UtcDateTime.Year * 12) + now.UtcDateTime.Month - 1;

    private static int? Parse(string value)
    {
        if (!Format().IsMatch(value))
        {
            return null;
        }

        var year = int.Parse(value[..4], CultureInfo.InvariantCulture);
        var month = int.Parse(value[4..], CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 ? (year * 12) + month - 1 : null;
    }

    // ^\d{6}$ in ASCII: .NET's \d also matches other scripts' digits, and \z, unlike $, refuses a trailing newline.
    [GeneratedRegex(@"^[0-9]{6}\z")]
    private static partial Regex Format();

    [GeneratedRegex(@"version .* is not active", RegexOptions.IgnoreCase)]
    private static partial Regex NotActive();
}

/// <summary>
/// Checks an API Version field with <see cref="LinkedInApiVersion.Problem"/>. Automate runs a settings property's validation attributes
/// when it resolves the connection's settings (Test connection, and every step that uses the connection), and shows the message.
/// </summary>
/// <remarks>
/// Automate 17.5 has no public hook that rejects a connection's settings on save: <c>IConnectionSettingsSaveHandler</c> is internal (only
/// Automate's own OAuth binding uses it), and a cancelled <c>ConnectionSavingNotification</c> reaches the backoffice as a bare "Server
/// Error". The backoffice's Test connection saves first and then resolves the settings, so a bad value is reported there. Replace this
/// with a save-time check when Automate offers one.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LinkedInApiVersionAttribute : ValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        => LinkedInApiVersion.Problem(value as string, DateTimeOffset.UtcNow) is { } problem
            ? new ValidationResult(problem, validationContext.MemberName is { } member ? [member] : null)
            : ValidationResult.Success;
}
