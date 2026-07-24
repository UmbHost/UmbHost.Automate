using System.Net.Http.Json;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teamwork.Connection;
using UmbHost.Automate.Teamwork.Http;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Adds a comment to a Teamwork task.
/// </summary>
/// <remarks>
/// Posts to the v3 <c>tasks/{id}/comments.json</c> endpoint with <c>{ "comment": { "body": ... } }</c>.
/// The exact request/response shape is worth confirming against a live Teamwork site (see plan open items).
/// </remarks>
[Action("teamwork.addTaskComment", "Add Teamwork Task Comment",
    Description = "Adds a comment to a Teamwork task.",
    Group = "Project Management",
    Icon = "icon-chat",
    ConnectionTypeAlias = "teamwork")]
public sealed class AddTaskCommentAction : ActionBase<AddTaskCommentSettings, AddTaskCommentOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AddTaskCommentAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<AddTaskCommentSettings>();
        if (string.IsNullOrWhiteSpace(settings.TaskId) || string.IsNullOrWhiteSpace(settings.Body))
        {
            return ActionResult.Failed(new ArgumentException("Task ID and body are required."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<TeamworkConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.ApiToken) || string.IsNullOrWhiteSpace(conn.Site))
        {
            return ActionResult.Failed(new InvalidOperationException("A Teamwork connection is required."), StepRunErrorCategory.Authentication);
        }

        using var client = TeamworkRequestHelper.CreateClient(_httpClientFactory, conn);
        using var response = await client.PostAsJsonAsync(
            $"tasks/{settings.TaskId}/comments.json",
            new { comment = new { body = settings.Body } },
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ActionResult.Failed(
                new InvalidOperationException($"Teamwork returned {(int)response.StatusCode}: {raw}"),
                StepRunErrorCategory.InvalidResponse);
        }

        long commentId = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("comment", out var comment) && comment.TryGetProperty("id", out var id))
            {
                commentId = id.ValueKind == JsonValueKind.Number ? id.GetInt64() : long.TryParse(id.GetString(), out var n) ? n : 0;
            }
        }
        catch (JsonException)
        {
            // Comment id is informational; the comment was created.
        }

        return Success(new AddTaskCommentOutput { CommentId = commentId });
    }
}
