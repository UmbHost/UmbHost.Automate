using System.Net.Http.Headers;
using System.Net.Http.Json;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GitHub.Connection;

namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Opens or closes a GitHub issue or pull request via the REST API.
/// </summary>
[Action("github.setIssueState", "Set GitHub Issue State",
    Description = "Opens or closes a GitHub issue or pull request.",
    Group = "Development",
    Icon = "icon-git",
    ConnectionTypeAlias = "github")]
public sealed class SetIssueStateAction : ActionBase<SetIssueStateSettings, SetIssueStateOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;

    public SetIssueStateAction(
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
        var s = context.GetSettings<SetIssueStateSettings>();
        var state = s.State?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(s.RepositoryFullName)
            || string.IsNullOrWhiteSpace(s.IssueNumber)
            || (state is not "open" and not "closed"))
        {
            return ActionResult.Failed(
                new ArgumentException("Repository, issue number and a state of 'open' or 'closed' are required."),
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

        var url = $"https://api.github.com/repos/{s.RepositoryFullName}/issues/{s.IssueNumber}";
        using var response = await client.PatchAsJsonAsync(url, new { state }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return ActionResult.Failed(
                new InvalidOperationException($"GitHub returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        return Success(new SetIssueStateOutput { State = state });
    }
}
