using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using UmbHost.Automate.Teams.Cards;

namespace UmbHost.Automate.Teams.Http;

/// <summary>
/// Posts a card to a Teams Workflows webhook. The address is a secret: it is re-checked here
/// before every send, and nothing here puts it (or its path or query) in a message.
/// </summary>
internal sealed class TeamsWebhookClient
{
    internal const string ClientName = "UmbHost.Automate.Teams";

    private readonly IHttpClientFactory _httpClientFactory;

    public TeamsWebhookClient(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    /// <summary>
    /// Registers <see cref="ClientName"/>. A redirect is reported, never followed, so a webhook
    /// can't send the card (and its signature) on to another host.
    /// </summary>
    public static void Register(IServiceCollection services)
        => services.AddHttpClient(ClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
                   .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    public async Task<HttpResponseMessage> PostAsync(Uri address, JsonObject message, CancellationToken cancellationToken)
    {
        if (!TeamsWebhookAddress.TryParse(address.OriginalString, out var checkedAddress, out _) || checkedAddress is null)
        {
            throw new InvalidOperationException(
                $"The Teams webhook address for host {TeamsWebhookAddress.HostOf(address.OriginalString)} is not allowed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, checkedAddress)
        {
            // The exact bytes TeamsCard produced: never re-serialised with other options.
            Content = new ByteArrayContent(TeamsCard.ToWireBytes(message)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var client = _httpClientFactory.CreateClient(ClientName);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
