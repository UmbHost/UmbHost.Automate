using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.Pinterest.Configuration;

namespace UmbHost.Automate.Pinterest.Connection;

/// <summary>
/// Pinterest connection type, signing in with OAuth via the OpenIddict client (<see cref="PinterestOAuthComposer"/>) or with an
/// access token from the app page, against production or the sandbox (<see cref="PinterestConnectionSettings"/>). Testing the connection
/// lists the account's boards as "Name (id)", so the board id a channel needs can be copied from there.
/// </summary>
[ConnectionType("pinterest", "Pinterest",
    Group = "Social",
    Icon = "icon-umbhost-pinterest",
    Description = "Connect to Pinterest to create Pins on your boards")]
public sealed class PinterestConnectionType : OAuthConnectionTypeBase<PinterestConnectionSettings>
{
    // GET /v5/boards is paged by bookmark. Sandbox "can return an empty items array with a bookmark even when Sandbox boards exist"
    // (https://developers.pinterest.com/docs/developer-tools/sandbox/), so an empty page doesn't end the walk; the largest page size
    // (250, same page) keeps the empty pages few, and the cap keeps a bookmark that never runs out from looping forever.
    internal const int PageSize = 250;
    internal const int MaxPages = 20;

    private readonly IHttpClientFactory _httpClientFactory;

    public PinterestConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
        => _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    public override string ProviderName => PinterestOAuthComposer.ProviderName;

    /// <inheritdoc />
    public override string? SetupDocsUrl => "https://developers.pinterest.com/apps/";

    /// <inheritdoc />
    /// <remarks>
    /// One connection type serves both sign-ins: the OAuth field is shown only for OAuth (<c>VisibleWhen</c>), and the base class's
    /// credential check runs only then. An access token skips it, so no Authenticate step (or OAuth client id) is needed.
    /// </remarks>
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        if (settings is not PinterestConnectionSettings conn)
        {
            return await base.ValidateAsync(settings, cancellationToken);
        }

        var kind = PinterestApi.Describe(conn);
        if (conn.SignIn == PinterestSignIn.OAuth && conn.Environment == PinterestEnvironment.Production)
        {
            var oauth = await base.ValidateAsync(settings, cancellationToken);
            if (oauth.Status != ConnectionValidationStatus.Success)
            {
                return oauth;
            }
        }

        var (accessToken, error) = await PinterestApi.GetTokenAsync(conn, CredentialsService, cancellationToken);
        if (accessToken is null)
        {
            return ConnectionValidationResult.Failure($"{kind}: {error}");
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var boards = new List<string>();
        string? bookmark = null;
        var exhausted = false;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{PinterestApi.BaseUrl(conn)}boards?page_size={PageSize}" + (bookmark is null ? string.Empty : "&bookmark=" + Uri.EscapeDataString(bookmark));
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && conn.SignIn == PinterestSignIn.AccessToken)
            {
                return ConnectionValidationResult.Failure($"{kind}: {PinterestApi.AccessTokenRejectedMessage(conn)}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ConnectionValidationResult.Failure($"{kind}: Pinterest returned {(int)response.StatusCode} listing the boards.", [body]);
            }

            using var doc = JsonDocument.Parse(body);
            foreach (var board in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                boards.Add($"{board.GetProperty("name").GetString()} ({board.GetProperty("id").GetString()})");
            }

            // Keep following the bookmark even after an empty page (the sandbox quirk above).
            bookmark = doc.RootElement.TryGetProperty("bookmark", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (string.IsNullOrEmpty(bookmark))
            {
                exhausted = true;
                break;
            }
        }

        var more = exhausted ? string.Empty : $"\n(Stopped after {MaxPages} pages; there may be more boards.)";
        return boards.Count == 0
            ? ConnectionValidationResult.Warning($"{kind}: no boards found{(exhausted ? string.Empty : $" in {MaxPages} pages")}. Create one in Pinterest first.", null)
            : ConnectionValidationResult.Success($"{kind}: Pinterest is connected. Use one of these board ids in the channel:\n" + string.Join("\n", boards) + more);
    }
}
