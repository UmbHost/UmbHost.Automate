using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.GitHub.Actions;

/// <summary>
/// Settings for the <see cref="RenameIssueTitleAction"/>.
/// </summary>
public sealed class RenameIssueTitleSettings
{
    [Field(Label = "Repository", Description = "owner/repo, e.g. umbhost/umbpanel.", SortOrder = 0, SupportsBindings = true)]
    public string RepositoryFullName { get; set; } = string.Empty;

    [Field(Label = "Issue Number", Description = "Issue or PR number.", SortOrder = 1, SupportsBindings = true)]
    public string IssueNumber { get; set; } = string.Empty;

    [Field(Label = "New Title", Description = "The new title.", SortOrder = 2, SupportsBindings = true)]
    public string NewTitle { get; set; } = string.Empty;
}
