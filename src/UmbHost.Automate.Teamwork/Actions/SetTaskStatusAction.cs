using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teamwork.Connection;
using UmbHost.Automate.Teamwork.Http;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Completes or reopens a Teamwork task.
/// </summary>
/// <remarks>
/// Calls the v3 <c>tasks/{id}/complete.json</c> / <c>tasks/{id}/uncomplete.json</c> endpoints.
/// Confirm the endpoint names against a live Teamwork site (see plan open items).
/// </remarks>
[Action("teamwork.setTaskStatus", "Set Teamwork Task Status",
    Description = "Completes or reopens a Teamwork task.",
    Group = "Project Management",
    Icon = "icon-checkbox",
    ConnectionTypeAlias = "teamwork")]
public sealed class SetTaskStatusAction : ActionBase<SetTaskStatusSettings, SetTaskStatusOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SetTaskStatusAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<SetTaskStatusSettings>();
        if (string.IsNullOrWhiteSpace(settings.TaskId))
        {
            return ActionResult.Failed(new ArgumentException("Task ID is required."), StepRunErrorCategory.Validation);
        }

        var reopen = settings.Status?.Trim().ToLowerInvariant() is "reopen" or "uncomplete" or "open";

        var conn = context.Connection?.GetSettings<TeamworkConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.ApiToken) || string.IsNullOrWhiteSpace(conn.Site))
        {
            return ActionResult.Failed(new InvalidOperationException("A Teamwork connection is required."), StepRunErrorCategory.Authentication);
        }

        var endpoint = reopen ? $"tasks/{settings.TaskId}/uncomplete.json" : $"tasks/{settings.TaskId}/complete.json";

        using var client = TeamworkRequestHelper.CreateClient(_httpClientFactory, conn);
        using var response = await client.PutAsync(endpoint, content: null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            return ActionResult.Failed(
                new InvalidOperationException($"Teamwork returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        return Success(new SetTaskStatusOutput { Status = reopen ? "reopened" : "completed" });
    }
}
