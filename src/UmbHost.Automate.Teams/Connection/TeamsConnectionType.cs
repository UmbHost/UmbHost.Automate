using System.Globalization;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Connections;
using Umbraco.Cms.Core.Configuration.Models;
using UmbHost.Automate.Teams.Cards;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Connection;

/// <summary>
/// Teams connection type. Test posts one test card to the Workflow; nothing else ever posts.
/// </summary>
[ConnectionType("teams", "Microsoft Teams",
    Description = "Post cards to a Teams channel or chat through a Workflows webhook",
    Group = "Social",
    Icon = "icon-microsoft")]
public sealed class TeamsConnectionType : ConnectionTypeBase<TeamsConnectionSettings>
{
    private readonly TeamsWebhookClient _client;
    private readonly IOptionsMonitor<WebRoutingSettings> _webRouting;
    private readonly TimeProvider _time;

    public TeamsConnectionType(
        ConnectionTypeInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<WebRoutingSettings> webRouting,
        TimeProvider? timeProvider = null)
        : base(infrastructure)
    {
        _client = new TeamsWebhookClient(httpClientFactory);
        _webRouting = webRouting;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not TeamsConnectionSettings typed)
        {
            return ConnectionValidationResult.Failure("A Teams workflow address is required.");
        }

        if (!TeamsWebhookAddress.TryParse(typed.WebhookUrl, out var address, out var error) || address is null)
        {
            return ConnectionValidationResult.Failure(error ?? "The Teams webhook address is not valid.");
        }

        var host = TeamsWebhookAddress.HostOf(typed.WebhookUrl);

        // Automate gives ValidateAsync no connection name, so the card can't name the connection.
        var body = string.Create(CultureInfo.InvariantCulture,
            $"This is a test from UmbSocial on {SiteName()}. Posts sent to this connection will appear here. Sent {_time.GetUtcNow():u}.");
        var card = TeamsCard.Message(body, null, null, null, null, "UmbSocial test");

        try
        {
            using var response = await _client.PostAsync(address, card, cancellationToken);
            var code = (int)response.StatusCode;
            if (code is 200 or 202)
            {
                return ConnectionValidationResult.Success(
                    "Teams accepted the test card. Check it appeared in the channel or chat.");
            }

            return ConnectionValidationResult.Failure(
                $"Teams answered {code} from {host}.",
                [$"Status {code} from {host}. If the Workflow was deleted or switched off, check it in Teams."]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // The exception text is deliberately not passed on: it could carry the address.
            return ConnectionValidationResult.Failure($"Could not reach {host}.");
        }
    }

    private string SiteName()
    {
        var url = _webRouting.CurrentValue.UmbracoApplicationUrl;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : "this site";
    }
}
