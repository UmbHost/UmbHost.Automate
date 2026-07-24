namespace UmbHost.Automate.Teamwork.Actions;

/// <summary>
/// Output produced by the <see cref="CreateTaskAction"/>. <see cref="TaskId"/> is bindable
/// in later steps as <c>${ steps.&lt;id&gt;.taskId }</c>.
/// </summary>
public sealed class CreateTaskOutput
{
    public long TaskId { get; init; }

    public string? TaskUrl { get; init; }
}
