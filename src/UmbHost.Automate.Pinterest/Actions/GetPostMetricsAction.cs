using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Pinterest.Actions;

/// <summary>
/// Reads each Pin's lifetime metrics under the names Pinterest's <c>pin_metrics</c> uses: <c>impression</c>, <c>save</c>,
/// <c>pin_click</c>, <c>outbound_click</c>, <c>reaction</c> and <c>comment</c> (and <c>clickthrough</c>, should Pinterest send it).
/// </summary>
/// <remarks>
/// <para>
/// One <c>GET /v5/pins/{pin_id}?pin_metrics=true</c> per id (operation <c>pins/get</c>, rate category <c>org_read</c>, scopes
/// <c>boards:read</c> and <c>pins:read</c>, which the connection already has). Source for the operation, the
/// <c>pin_metrics</c> parameter ("Total comments and total reactions are only available with lifetime Pin metrics. If Pin was
/// created before 2023-03-20 lifetime metrics will only be available for Video and Idea Pin formats.") and the
/// <c>Pin.pin_metrics</c> example (<c>90d</c> and <c>lifetime_metrics</c>):
/// https://raw.githubusercontent.com/pinterest/api-description/main/v5/openapi.yaml.
/// The spec types <c>pin_metrics</c> only as an object with that example, so only the known names are passed on, each only when
/// Pinterest sends it as a number. The example names outbound clicks <c>clickthrough</c>, but the live sandbox answer on 2026-11-18
/// sent <c>outbound_click</c> (and <c>save</c>, which the example lacks, plus <c>profile_visit</c> and <c>user_follow</c> as null), so
/// <c>outbound_click</c> is read and <c>clickthrough</c> is kept for an answer shaped like the example.
/// </para>
/// <para>
/// A 404 is <c>gone</c>; a 403 is <c>needsPermission</c> with <c>scope_missing</c> for that Pin; a Pin without lifetime metrics is
/// <c>unavailable</c> with <c>unsupported</c> (the 90-day block isn't a lifetime value). A 401, 429, 408 or 5xx fails the step.
/// Trial-tier apps get 1,000 requests a day in all: https://developers.pinterest.com/docs/reference/rate-limits.md.
/// </para>
/// </remarks>
[Action("pinterest.getPostMetrics", "Get Pinterest Pin Metrics",
    Description = "Reads your Pins' lifetime impressions, saves, Pin clicks, outbound clicks, reactions and comments.",
    Group = "Social",
    Icon = "icon-chart-curve",
    ConnectionTypeAlias = "pinterest")]
public sealed partial class GetPostMetricsAction : ActionBase<GetPostMetricsSettings, GetPostMetricsOutput>
{
    private const string Network = "Pinterest";

    private static readonly string[] MetricNames = ["impression", "save", "pin_click", "outbound_click", "clickthrough", "reaction", "comment"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public GetPostMetricsAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    // The spec's PinKey (^\d+$), without a leading zero.
    [GeneratedRegex(@"^[1-9][0-9]{0,29}\z")]
    private static partial Regex PinId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var ids = PostMetricsIds.Parse(context.GetSettings<GetPostMetricsSettings>().Ids);
        if (ids is null)
        {
            return PostMetricsIds.Invalid();
        }

        var conn = context.Connection?.GetSettings<PinterestConnectionSettings>();
        if (conn is null)
        {
            return ActionResult.Failed(new InvalidOperationException("A Pinterest connection is required."), StepRunErrorCategory.Authentication);
        }

        // Ids not settled here are gone: malformed (never sent) or 404.
        var settled = new Dictionary<string, PostMetricsResult>(StringComparer.Ordinal);
        var valid = ids.Where(id => PinId().IsMatch(id)).ToList();

        try
        {
            if (valid.Count > 0)
            {
                var (token, error) = await PinterestApi.GetTokenAsync(conn, _credentialsService, cancellationToken);
                if (token is null)
                {
                    return PinterestApi.NoToken(error!);
                }

                using var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                foreach (var id in valid)
                {
                    using var response = await client.GetAsync($"{PinterestApi.BaseUrl(conn)}pins/{id}?pin_metrics=true", cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        continue;
                    }

                    if (response.StatusCode == HttpStatusCode.Unauthorized && conn.SignIn == PinterestSignIn.AccessToken)
                    {
                        return PinterestApi.AccessTokenRejected(conn);
                    }

                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        settled[id] = PostMetricsResult.NeedsPermission(id, PostMetricsReason.ScopeMissing);
                        continue;
                    }

                    // Any other 4xx but 401, 408 and 429 settles this pin alone: the rest of the batch carries on.
                    if (ReadOutcome.RejectsThePost(response.StatusCode))
                    {
                        settled[id] = PostMetricsResult.Unavailable(id, PostMetricsReason.Rejected);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return ReadOutcome.FromResponse(context, Network, response.StatusCode, body);
                    }

                    try
                    {
                        settled[id] = Read(id, body);
                    }
                    catch (Exception ex) when (IsUnreadable(ex))
                    {
                        return Unreadable(ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ReadOutcome.IsTransport(ex))
        {
            return ReadOutcome.FromException(ex, cancellationToken);
        }

        var results = ids.Select(id => settled.TryGetValue(id, out var result) ? result : PostMetricsResult.Gone(id)).ToList();
        return Success(new GetPostMetricsOutput { Results = results });
    }

    /// <summary>The step failure for an answer that can't be read.</summary>
    private static ActionResult Unreadable(Exception ex)
        => ActionResult.Failed(new InvalidOperationException($"{Network} returned an unreadable answer.", ex), StepRunErrorCategory.InvalidResponse);

    // Only the parse of a successful answer is caught as unreadable, never the token lookup or the request.
    private static bool IsUnreadable(Exception ex) => ex is JsonException or KeyNotFoundException or InvalidOperationException;

    private static PostMetricsResult Read(string id, string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("pin_metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object
            && metrics.TryGetProperty("lifetime_metrics", out var lifetime) && lifetime.ValueKind == JsonValueKind.Object
            ? PostMetricsResult.Ok(id, ReadOutcome.Counts(lifetime, MetricNames))
            : PostMetricsResult.Unavailable(id, PostMetricsReason.Unsupported);
    }
}
