using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GoogleBusiness.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.GoogleBusiness.Actions;

/// <summary>
/// Posts an update (a <c>STANDARD</c> local post) to a Google Business Profile location, with an optional button and image.
/// </summary>
/// <remarks>
/// <para>
/// API: <c>POST https://mybusiness.googleapis.com/v4/{accounts/*/locations/*}/localPosts</c>
/// (https://developers.google.com/my-business/reference/rest/v4/accounts.locations.localPosts). Google fetches the image from its
/// public URL. The summary is limited to 1,500 characters: not stated by Google (neither the localPosts reference nor the Business
/// Profile help gives a limit); widely reported, and on the live-test backlog.
/// </para>
/// <para>
/// A location that isn't verified can't post, so the location's Voice of Merchant state is read first
/// (https://developers.google.com/my-business/reference/verifications/rest/v1/locations/getVoiceOfMerchantState) and a location without it
/// is a definite failure, <c>google_business.location_unverified</c>, before anything is sent. Google has no idempotency key, so a create
/// request with no clear answer is never retried (see <see cref="PostOutcome"/>).
/// </para>
/// </remarks>
[Action("googleBusiness.createUpdate", "Create Google Business Update",
    Description = "Posts an update to a Google Business Profile location.",
    Group = "Social",
    Icon = "icon-google",
    ConnectionTypeAlias = "google-business")]
public sealed partial class CreateUpdateAction : ActionBase<CreateUpdateSettings, CreateUpdateOutput>
{
    private const string Network = "Google Business Profile";
    internal const int MaxSummary = 1500;

    /// <summary>The start of the failure message for a location that isn't verified.</summary>
    public const string UnverifiedCode = "google_business.location_unverified";

    /// <summary>The buttons an update can have (the API's other value, GET_OFFER, belongs to offers, which aren't supported).</summary>
    public static readonly IReadOnlySet<string> ActionTypes = new HashSet<string>(StringComparer.Ordinal) { "BOOK", "ORDER", "SHOP", "SIGN_UP", "LEARN_MORE", "CALL" };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public CreateUpdateAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    [GeneratedRegex("^accounts/[0-9]{1,30}/(locations/[0-9]{1,30})$")]
    private static partial Regex LocationName();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateUpdateSettings>();

        var location = LocationName().Match(settings.LocationName ?? string.Empty);
        var actionType = string.IsNullOrWhiteSpace(settings.ActionType) ? "LEARN_MORE" : settings.ActionType.Trim();
        var invalid = !location.Success ? "Location must be accounts/<id>/locations/<id>. Test the connection to list your locations."
            : string.IsNullOrWhiteSpace(settings.Summary) ? "Summary is required."
            : settings.Summary.Length > MaxSummary ? $"An update is limited to {MaxSummary} characters."
            : !ActionTypes.Contains(actionType) ? $"Button must be one of {string.Join(", ", ActionTypes)}."
            : !IsHttpsOrBlank(settings.LinkUrl) ? "Link URL must be an absolute https URL."
            : !IsHttpsOrBlank(settings.ImageUrl) ? "Image URL must be an absolute https URL."
            : null;
        if (invalid is not null)
        {
            return ActionResult.Failed(new ArgumentException(invalid), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<GoogleBusinessConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A Google Business Profile connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The Google token is expired or revoked. Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var verified = await VerifiedAsync(client, location.Groups[1].Value, cancellationToken);
        if (verified is not null)
        {
            return verified;
        }

        var update = new JsonObject { ["topicType"] = "STANDARD", ["summary"] = settings.Summary };
        if (actionType == "CALL")
        {
            update["callToAction"] = new JsonObject { ["actionType"] = "CALL" };
        }
        else if (!string.IsNullOrWhiteSpace(settings.LinkUrl))
        {
            update["callToAction"] = new JsonObject { ["actionType"] = actionType, ["url"] = settings.LinkUrl };
        }

        if (!string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            update["media"] = new JsonArray(new JsonObject { ["mediaFormat"] = "PHOTO", ["sourceUrl"] = settings.ImageUrl });
        }

        string body;
        try
        {
            using var response = await client.PostAsJsonAsync($"https://mybusiness.googleapis.com/v4/{settings.LocationName}/localPosts", update, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PostOutcome.FromResponse(Network, response.StatusCode, body, idempotent: false);
            }
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        using var doc = JsonDocument.Parse(body);
        return Success(new CreateUpdateOutput
        {
            PostId = doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            PostUrl = doc.RootElement.TryGetProperty("searchUrl", out var url) ? url.GetString() ?? string.Empty : string.Empty,
        });
    }

    /// <summary>Null when the location has Voice of Merchant (it can post); otherwise the failure.</summary>
    private static async Task<ActionResult?> VerifiedAsync(HttpClient client, string location, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"https://mybusinessverifications.googleapis.com/v1/{location}/VoiceOfMerchantState", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return PostOutcome.FromPreparationResponse(Network, "verification check", response.StatusCode, body);
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("hasVoiceOfMerchant", out var voice) && voice.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        return ActionResult.Failed(
            new InvalidOperationException($"{UnverifiedCode}: the location isn't verified in Google Business Profile, so it can't post updates yet. Verify it in Google Business Profile, then post again."),
            StepRunErrorCategory.Validation);
    }

    private static bool IsHttpsOrBlank(string? url)
        => string.IsNullOrWhiteSpace(url) || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
}
