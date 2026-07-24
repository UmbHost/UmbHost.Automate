using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GitHub.Connection;

namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Updates the title of a GitHub issue or pull request via the REST API.
/// </summary>
[Action("github.renameIssueTitle", "Rename GitHub Issue Title",
    Description = "Updates the title of a GitHub issue or pull request.",
    Group = "Development",
    Icon = "icon-git",
    ConnectionTypeAlias = "github")]
public sealed class RenameIssueTitleAction : ActionBase<RenameIssueTitleSettings, RenameIssueTitleOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public RenameIssueTitleAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var s = context.GetSettings<RenameIssueTitleSettings>();
        if (string.IsNullOrWhiteSpace(s.RepositoryFullName)
            || string.IsNullOrWhiteSpace(s.IssueNumber)
            || string.IsNullOrWhiteSpace(s.NewTitle))
        {
            return ActionResult.Failed(
                new ArgumentException("Repository, issue number and new title are required."),
                StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<GitHubConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credId || credId == Guid.Empty)
        {
            return ActionResult.Failed(
                new InvalidOperationException("GitHub is not authenticated."),
                StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credId, cancellationToken);
        if (token is null)
        {
            return ActionResult.Failed(
                new InvalidOperationException("GitHub access token is expired or revoked. Please re-authenticate."),
                StepRunErrorCategory.Authentication);
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UmbHost.Automate.GitHub");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url = $"https://api.github.com/repos/{s.RepositoryFullName}/issues/{s.IssueNumber}";
        using var response = await client.PatchAsJsonAsync(url, new { title = s.NewTitle }, cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ActionResult.Failed(
                new InvalidOperationException($"GitHub returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        var number = long.TryParse(s.IssueNumber, out var n) ? n : 0;
        string? htmlUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("html_url", out var h))
            {
                htmlUrl = h.GetString();
            }
        }
        catch (JsonException)
        {
            // Response body is informational only; the PATCH already succeeded.
        }

        return Success(new RenameIssueTitleOutput { IssueNumber = number, HtmlUrl = htmlUrl });
    }
}
