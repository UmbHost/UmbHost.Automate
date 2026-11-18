using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;
using UmbHost.Automate.Teams.Cards;
using UmbHost.Automate.Teams.Connection;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Actions;

/// <summary>
/// Posts a card to a Teams channel or chat through a Workflows webhook.
/// </summary>
/// <remarks>
/// Teams has no idempotency key, so an unconfirmed request (5xx, timeout, network error) is
/// reported as terminal with the "may have posted" message rather than retried. The webhook
/// address is a secret: failures name the host and status only, and Teams' response body is never
/// passed on (its error text can carry the workflow id, which is part of the secret path).
/// </remarks>
[Action("teams.postCard", "Post a Card to Microsoft Teams",
    Description = "Posts a card to a Teams channel or chat through a Workflows webhook.",
    Group = "Social",
    Icon = "icon-microsoft",
    ConnectionTypeAlias = "teams")]
public sealed partial class PostCardAction : ActionBase<PostCardSettings, PostCardOutput>
{
    private const string Network = "Microsoft Teams";

    private readonly TeamsWebhookClient _client;

    public PostCardAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
        => _client = new TeamsWebhookClient(httpClientFactory);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<PostCardSettings>();

        if (string.IsNullOrWhiteSpace(settings.Text))
        {
            return ActionResult.Failed(new ArgumentException("Text is required."), StepRunErrorCategory.Validation);
        }

        if (!IsBlankOrHttps(settings.ImageUrl))
        {
            return ActionResult.Failed(new ArgumentException("The image URL must be an https address."), StepRunErrorCategory.Validation);
        }

        if (!IsBlankOrHttps(settings.LinkUrl))
        {
            return ActionResult.Failed(new ArgumentException("The link URL must be an https address."), StepRunErrorCategory.Validation);
        }

        var connection = context.Connection?.GetSettings<TeamsConnectionSettings>();
        if (connection is null)
        {
            return ActionResult.Failed(new InvalidOperationException("A Teams connection is required."), StepRunErrorCategory.Authentication);
        }

        if (!TeamsWebhookAddress.TryParse(connection.WebhookUrl, out var address, out var addressError) || address is null)
        {
            // TryParse's messages name the host at most, never the path or query.
            return ActionResult.Failed(
                new InvalidOperationException(addressError ?? "The Teams webhook address is not valid."),
                StepRunErrorCategory.Authentication);
        }

        var host = TeamsWebhookAddress.HostOf(connection.WebhookUrl);
        var message = TeamsCard.Message(settings.Text, Blank(settings.ImageUrl), settings.ImageAltText, Blank(settings.LinkUrl), settings.LinkTitle);
        if (TeamsCard.SizeOf(message) > TeamsCard.MaxBytes)
        {
            return ActionResult.Failed(
                new ArgumentException($"The card is larger than Teams allows ({TeamsCard.MaxBytes:N0} bytes). Shorten the text."),
                StepRunErrorCategory.Validation);
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.PostAsync(address, message, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }
        catch (Exception)
        {
            // Replaced, not wrapped: the original's text or inner exceptions could carry the address.
            return PostOutcome.FromException(Network, new HttpRequestException($"Could not reach {host}."), idempotent: false);
        }

        using (response)
        {
            var status = response.StatusCode;
            var code = (int)status;
            if (code is >= 200 and <= 299)
            {
                return Success(new PostCardOutput
                {
                    Accepted = true,
                    Note = "Teams accepted the post. It can't confirm it appeared: if it didn't, check the Workflow's run history in Teams.",
                });
            }

            if (code is >= 300 and <= 399)
            {
                // The client never follows a redirect, so nothing reached Teams: terminal, and not "may have posted".
                return ActionResult.Failed(
                    new InvalidOperationException($"Teams answered with a redirect, which is never followed. Check the Workflow address. (status {code} from {host})"),
                    StepRunErrorCategory.Validation);
            }

            var detail = $"status {code} from {host}" + await ErrorCodeAsync(response, cancellationToken);

            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Validation, not Authentication: a Workflows webhook has no sign-in, and Authentication
                // makes Social tell the editor to sign in again.
                return ActionResult.Failed(
                    new InvalidOperationException($"Teams refused the post: the Workflow's address no longer works, or its trigger isn't set to Anyone. ({detail})"),
                    StepRunErrorCategory.Validation);
            }

            if (status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return ActionResult.Failed(
                    new InvalidOperationException($"Teams refused the post: the Workflow may have been deleted or switched off. ({detail})"),
                    StepRunErrorCategory.Validation);
            }

            // The detail stands in for the body: PostOutcome decides the category and the "may have posted" wording.
            // The headers carry Retry-After on a 429, so a throttled Workflow says when to try again.
            return PostOutcome.FromResponse(context, Network, status, detail, idempotent: false, headers: response.Headers);
        }
    }

    private static bool IsBlankOrHttps(string? value)
        => string.IsNullOrWhiteSpace(value)
           || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Teams' <c>error.code</c>, only when the body is JSON and the code is a short plain token.
    /// Nothing else from the body is ever used.
    /// </summary>
    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } text && PlainCode().IsMatch(text))
            {
                return $", error {text}";
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException or IOException)
        {
        }

        return string.Empty;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex PlainCode();
}
