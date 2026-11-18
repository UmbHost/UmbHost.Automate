using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Connection;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Pinterest.Actions;

/// <summary>
/// Creates a Pin on a board (Pinterest API v5, <c>POST /v5/pins</c>). Pinterest fetches the image from its public URL, so nothing is
/// uploaded first.
/// </summary>
/// <remarks>
/// Limits from Pinterest's API description (https://github.com/pinterest/api-description/blob/main/v5/openapi.yaml, <c>PinCreate</c>):
/// title 100, description 800, link 2048, alt text 500. Pinterest has no idempotency key, so a create request with no clear answer is
/// never retried (see <see cref="PostOutcome"/>).
/// </remarks>
[Action("pinterest.createPin", "Create Pin",
    Description = "Creates a Pin on one of your Pinterest boards.",
    Group = "Social",
    Icon = "icon-umbhost-pinterest",
    ConnectionTypeAlias = "pinterest")]
public sealed partial class CreatePinAction : ActionBase<CreatePinSettings, CreatePinOutput>
{
    private const string Network = "Pinterest";
    internal const int MaxTitle = 100;
    internal const int MaxDescription = 800;
    internal const int MaxLink = 2048;
    internal const int MaxAltText = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public CreatePinAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory, IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    [GeneratedRegex("^[0-9]{1,30}$")]
    private static partial Regex BoardId();

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreatePinSettings>();

        var invalid = Validate(settings);
        if (invalid is not null)
        {
            return ActionResult.Failed(new ArgumentException(invalid), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<PinterestConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A Pinterest connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The Pinterest token is expired or revoked. Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        var pin = new JsonObject
        {
            ["board_id"] = settings.BoardId,
            ["media_source"] = new JsonObject { ["source_type"] = "image_url", ["url"] = settings.ImageUrl },
        };
        AddIfSet(pin, "title", settings.Title);
        AddIfSet(pin, "description", settings.Description);
        AddIfSet(pin, "link", settings.Link);
        AddIfSet(pin, "alt_text", settings.ImageAltText);

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string body;
        try
        {
            using var response = await client.PostAsJsonAsync("https://api.pinterest.com/v5/pins", pin, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PostOutcome.FromResponse(context, Network, response.StatusCode, body, idempotent: false, headers: response.Headers);
            }
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
        return Success(new CreatePinOutput { PostId = id, PostUrl = id.Length > 0 ? $"https://www.pinterest.com/pin/{id}/" : string.Empty });
    }

    private static string? Validate(CreatePinSettings settings)
    {
        if (!BoardId().IsMatch(settings.BoardId ?? string.Empty))
        {
            return "Board ID must be the board's numeric id. Test the connection to list your boards.";
        }

        if (string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            return "Pinterest needs an image: every Pin is an image.";
        }

        if (!Uri.TryCreate(settings.ImageUrl, UriKind.Absolute, out var image) || image.Scheme != Uri.UriSchemeHttps)
        {
            return "Image URL must be an absolute https URL.";
        }

        return settings.Title?.Length > MaxTitle ? $"A Pin title is limited to {MaxTitle} characters."
            : settings.Description?.Length > MaxDescription ? $"A Pin description is limited to {MaxDescription} characters."
            : settings.Link?.Length > MaxLink ? $"A Pin link is limited to {MaxLink} characters."
            : settings.ImageAltText?.Length > MaxAltText ? $"Pin alt text is limited to {MaxAltText} characters."
            : null;
    }

    private static void AddIfSet(JsonObject json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            json[name] = value;
        }
    }
}
