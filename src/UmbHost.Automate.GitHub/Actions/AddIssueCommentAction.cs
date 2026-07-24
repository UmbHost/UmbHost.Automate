using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GitHub.Connection;

namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Adds a comment to a GitHub issue or pull request via the REST API.
/// </summary>
[Action("github.addIssueComment", "Add GitHub Issue Comment",
    Description = "Adds a comment to a GitHub issue or pull request.",
    Group = "Development",
    Icon = "icon-chat",
    ConnectionTypeAlias = "github")]
public sealed class AddIssueCommentAction : ActionBase<AddIssueCommentSettings, AddIssueCommentOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public AddIssueCommentAction(
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
        var s = context.GetSettings<AddIssueCommentSettings>();
        if (string.IsNullOrWhiteSpace(s.RepositoryFullName)
            || string.IsNullOrWhiteSpace(s.IssueNumber)
            || string.IsNullOrWhiteSpace(s.Body))
        {
            return ActionResult.Failed(
                new ArgumentException("Repository, issue number and body are required."),
                StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<GitHubConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credId || credId == Guid.Empty)
        {
            return ActionResult.Failed(new InvalidOperationException("GitHub is not authenticated."), StepRunErrorCategory.Authentication);
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

        var url = $"https://api.github.com/repos/{s.RepositoryFullName}/issues/{s.IssueNumber}/comments";
        using var response = await client.PostAsJsonAsync(url, new { body = s.Body }, cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ActionResult.Failed(
                new InvalidOperationException($"GitHub returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        long commentId = 0;
        string? htmlUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            {
                commentId = id.GetInt64();
            }

            if (doc.RootElement.TryGetProperty("html_url", out var h))
            {
                htmlUrl = h.GetString();
            }
        }
        catch (JsonException)
        {
            // Response body is informational; the comment was created.
        }

        return Success(new AddIssueCommentOutput { CommentId = commentId, HtmlUrl = htmlUrl });
    }
}
