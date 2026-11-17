using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Configuration;

namespace UmbHost.Automate.Pinterest.Connection;

/// <summary>
/// Pinterest connection type using OAuth via the OpenIddict client (<see cref="PinterestOAuthComposer"/>). Testing the connection
/// lists the account's boards as "Name (id)", so the board id a channel needs can be copied from there.
/// </summary>
[ConnectionType("pinterest", "Pinterest",
    Group = "Social",
    Icon = "icon-umbhost-pinterest",
    Description = "Connect to Pinterest to create Pins on your boards")]
public sealed class PinterestConnectionType : OAuthConnectionTypeBase<PinterestConnectionSettings>
{
    // GET /v5/boards is paged by bookmark; a few pages cover any real account without an unbounded loop.
    private const int MaxPages = 10;

    private readonly IHttpClientFactory _httpClientFactory;

    public PinterestConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
        => _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    public override string ProviderName => PinterestOAuthComposer.ProviderName;

    /// <inheritdoc />
    public override string? SetupDocsUrl => "https://developers.pinterest.com/apps/";

    /// <inheritdoc />
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var token = await base.ValidateAsync(settings, cancellationToken);
        if (token.Status != ConnectionValidationStatus.Success || settings is not PinterestConnectionSettings { OAuthCredentialsId: { } id })
        {
            return token;
        }

        var accessToken = await CredentialsService.GetValidAccessTokenAsync(id, cancellationToken);
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var boards = new List<string>();
        string? bookmark = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = "https://api.pinterest.com/v5/boards?page_size=100" + (bookmark is null ? string.Empty : "&bookmark=" + Uri.EscapeDataString(bookmark));
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"Pinterest returned {(int)response.StatusCode} listing the boards.", [body]);
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var board in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                boards.Add($"{board.GetProperty("name").GetString()} ({board.GetProperty("id").GetString()})");
            }

            bookmark = doc.RootElement.TryGetProperty("bookmark", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (string.IsNullOrEmpty(bookmark))
            {
                break;
            }
        }

        return boards.Count == 0
            ? ConnectionValidationResult.Warning("No boards found: create one in Pinterest first.", null)
            : ConnectionValidationResult.Success("Pinterest is connected. Use one of these board ids in the channel:\n" + string.Join("\n", boards));
    }
}
