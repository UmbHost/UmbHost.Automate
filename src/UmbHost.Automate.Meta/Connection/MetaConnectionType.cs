using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Umbraco.Automate.Core.Connections;
using UmbHost.Automate.Meta.Configuration;
using UmbHost.Automate.Meta.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Connection;

/// <summary>
/// Meta connection type for a Facebook Page and its linked Instagram account. Validates the Page
/// token and reports the linked Instagram account id so it can be copied into the connection.
/// </summary>
/// <remarks>
/// While <c>Umbraco:Automate:Providers:Meta:Insights</c> is on, Validate also reads <c>GET /me/permissions</c>
/// (<c>data[] {permission, status}</c>, status granted, declined or expired:
/// https://developers.facebook.com/docs/graph-api/reference/user/permissions/) and warns about any of <c>read_insights</c>,
/// <c>pages_read_engagement</c>, <c>pages_read_user_content</c> and, with a linked Instagram account,
/// <c>instagram_manage_insights</c> the token lacks. The page documents <c>/{user-id}/permissions</c>; reading it through
/// <c>/me</c> with a Page token is <b>UNVERIFIED</b>, so a permissions call that fails or can't be read is a warning too, never a failure.
/// While the switch is off nothing is checked: the permissions only matter to the metrics.
/// </remarks>
[ConnectionType("meta", "Facebook & Instagram",
    Description = "Connect to a Facebook Page (and its linked Instagram account) using a Page access token",
    Group = "Social",
    Icon = "icon-facebook")]
public sealed class MetaConnectionType : ConnectionTypeBase<MetaConnectionSettings>
{
    /// <summary>What the insights reads need (https://developers.facebook.com/docs/permissions and each metrics action's sources).</summary>
    private static readonly string[] PageInsightsPermissions = ["read_insights", "pages_read_engagement", "pages_read_user_content"];

    private const string InstagramInsightsPermission = "instagram_manage_insights";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public MetaConnectionType(ConnectionTypeInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not MetaConnectionSettings typed
            || string.IsNullOrWhiteSpace(typed.PageId)
            || string.IsNullOrWhiteSpace(typed.PageAccessToken))
        {
            return ConnectionValidationResult.Failure("A Page ID and Page access token are required.");
        }

        using var client = GraphClient.Create(_httpClientFactory, typed);
        try
        {
            using var response = await client.GetAsync($"{typed.PageId}?fields=name,instagram_business_account", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"Meta rejected the token or Page ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : typed.PageId;
            var instagram = doc.RootElement.TryGetProperty("instagram_business_account", out var ig)
                ? ig.GetProperty("id").GetString()
                : null;

            var message = instagram is null
                ? $"Connected to the Page '{name}'. No Instagram account is linked to it."
                : instagram == typed.InstagramAccountId
                    ? $"Connected to the Page '{name}' and its Instagram account."
                    : $"Connected to the Page '{name}'. Its linked Instagram account id is {instagram}; enter it to post to Instagram.";

            if (!InsightsSwitch.IsOn(_configuration, MetaInsights.Setting))
            {
                return ConnectionValidationResult.Success(message);
            }

            var needed = instagram is null ? PageInsightsPermissions : [.. PageInsightsPermissions, InstagramInsightsPermission];
            var missing = await MissingAsync(client, needed, cancellationToken);
            return missing switch
            {
                null => ConnectionValidationResult.Warning(
                    $"{message} Its insights permissions couldn't be checked, so post metrics may come back as needing permission.", null),
                [] => ConnectionValidationResult.Success(message),
                _ => ConnectionValidationResult.Warning(
                    $"{message} Post metrics need permissions this token lacks: {string.Join(", ", missing)}. Generate a new Page token with them.",
                    missing),
            };
        }
        catch (Exception ex)
        {
            return ConnectionValidationResult.Failure("Could not reach the Graph API.", [ex.Message]);
        }
    }

    /// <summary>The permissions in <paramref name="needed"/> the token hasn't been granted; null when they can't be read.</summary>
    private static async Task<IReadOnlyList<string>?> MissingAsync(HttpClient client, IReadOnlyList<string> needed, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync("me/permissions", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var granted = doc.RootElement.GetProperty("data").EnumerateArray()
                .Where(p => p.TryGetProperty("status", out var status) && status.GetString() == "granted")
                .Select(p => p.GetProperty("permission").GetString())
                .ToHashSet(StringComparer.Ordinal);
            return needed.Where(p => !granted.Contains(p)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
