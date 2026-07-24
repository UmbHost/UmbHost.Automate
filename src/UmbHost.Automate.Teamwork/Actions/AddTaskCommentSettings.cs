using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Settings for the <see cref="AddTaskCommentAction"/>.
/// </summary>
public sealed class AddTaskCommentSettings
{
    [Field(Label = "Task ID", Description = "The Teamwork task to comment on.", SortOrder = 0, SupportsBindings = true)]
    public string TaskId { get; set; } = string.Empty;

    [Field(Label = "Body", Description = "The comment body (HTML allowed).", SortOrder = 1, SupportsBindings = true)]
    public string Body { get; set; } = string.Empty;
}
