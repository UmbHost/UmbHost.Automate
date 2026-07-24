using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Settings for the <see cref="CreateTaskAction"/>.
/// </summary>
public sealed class CreateTaskSettings
{
    [Field(Label = "Tasklist ID", Description = "The Teamwork tasklist the task is created in.", SortOrder = 0)]
    public string TasklistId { get; set; } = string.Empty;

    [Field(Label = "Name", Description = "Task name.", SortOrder = 1, SupportsBindings = true)]
    public string Name { get; set; } = string.Empty;

    [Field(Label = "Description", Description = "Task description (HTML allowed).", SortOrder = 2, SupportsBindings = true)]
    public string? Description { get; set; }

    [Field(Label = "Priority", Description = "low | medium | high (optional).", SortOrder = 3, SupportsBindings = true)]
    public string? Priority { get; set; }

    [Field(Label = "Tags", Description = "Comma-separated tag names (optional).", SortOrder = 4, SupportsBindings = true)]
    public string? Tags { get; set; }
}
