using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Settings for the <see cref="SetTaskStatusAction"/>.
/// </summary>
public sealed class SetTaskStatusSettings
{
    [Field(Label = "Task ID", Description = "The Teamwork task to update.", SortOrder = 0, SupportsBindings = true)]
    public string TaskId { get; set; } = string.Empty;

    [Field(Label = "Status", Description = "complete | reopen.", SortOrder = 1, SupportsBindings = true)]
    public string Status { get; set; } = "complete";
}
