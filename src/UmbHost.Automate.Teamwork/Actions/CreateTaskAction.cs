using System.Net.Http.Json;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teamwork.Connection;
using UmbHost.Automate.Teamwork.Http;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Creates a task in a Teamwork tasklist and returns the created task id.
/// </summary>
[Action("teamwork.createTask", "Create Teamwork Task",
    Description = "Creates a task in a Teamwork tasklist.",
    Group = "Project Management",
    Icon = "icon-checkbox",
    ConnectionTypeAlias = "teamwork")]
public sealed class CreateTaskAction : ActionBase<CreateTaskSettings, CreateTaskOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CreateTaskAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateTaskSettings>();

        if (string.IsNullOrWhiteSpace(settings.TasklistId))
        {
            return ActionResult.Failed(new ArgumentException("Tasklist ID is required."), StepRunErrorCategory.Validation);
        }

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            return ActionResult.Failed(new ArgumentException("Name is required."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<TeamworkConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.ApiToken) || string.IsNullOrWhiteSpace(conn.Site))
        {
            return ActionResult.Failed(new InvalidOperationException("A Teamwork connection is required."), StepRunErrorCategory.Authentication);
        }

        var task = new Dictionary<string, object?> { ["name"] = settings.Name };
        if (!string.IsNullOrWhiteSpace(settings.Description))
        {
            task["description"] = settings.Description;
        }

        if (!string.IsNullOrWhiteSpace(settings.Priority))
        {
            task["priority"] = settings.Priority;
        }

        if (!string.IsNullOrWhiteSpace(settings.Tags))
        {
            task["tags"] = settings.Tags;
        }

        using var client = TeamworkRequestHelper.CreateClient(_httpClientFactory, conn);
        using var response = await client.PostAsJsonAsync(
            $"tasklists/{settings.TasklistId}/tasks.json", new { task }, cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ActionResult.Failed(
                new InvalidOperationException($"Teamwork returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        long taskId;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            taskId = doc.RootElement.GetProperty("task").GetProperty("id").GetInt64();
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(
                new InvalidOperationException($"Could not read created task id from: {raw}", ex),
                StepRunErrorCategory.InvalidResponse);
        }

        return Success(new CreateTaskOutput
        {
            TaskId = taskId,
            TaskUrl = $"https://{conn.Site}.teamwork.com/app/tasks/{taskId}",
        });
    }
}
