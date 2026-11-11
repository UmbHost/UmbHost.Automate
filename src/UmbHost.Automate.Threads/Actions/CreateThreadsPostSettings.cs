using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Threads.Actions;

/// <summary>
/// Settings for the <see cref="CreateThreadsPostAction"/>.
/// </summary>
public sealed class CreateThreadsPostSettings
{
    [Field(Label = "Text", Description = "Post text, 500 characters at most (Threads counts each emoji as its UTF-8 bytes). Links in the text are clickable.", SortOrder = 0, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Image URL", Description = "Optional public https URL of a JPEG or PNG image (8 MB max), which Threads fetches.", SortOrder = 1, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers (1000 characters max).", SortOrder = 2, SupportsBindings = true)]
    public string? ImageAltText { get; set; }

    [Field(Label = "Thread Parts", Description = "Optional JSON array of further posts, e.g. [\"Second\",\"Third\"] (max 10). Each is posted as a reply to the one before.", SortOrder = 3, SupportsBindings = true)]
    public string? ThreadParts { get; set; }
}
