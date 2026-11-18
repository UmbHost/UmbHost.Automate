using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.X.Actions;

/// <summary>
/// Settings for the <see cref="CreatePostAction"/>.
/// </summary>
public sealed class CreatePostSettings
{
    [Field(Label = "Text", Description = "Post text, 280 weighted characters (links count as 23). X bills a post containing a link at a higher rate.", SortOrder = 0, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Image URL", Description = "Optional https URL of a JPEG, PNG or WebP image (5 MB max), or a GIF (15 MB max).", SortOrder = 1, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers (1000 characters max).", SortOrder = 2, SupportsBindings = true)]
    public string? ImageAltText { get; set; }

    [Field(Label = "Thread Parts", Description = "Optional JSON array of further posts, e.g. [\"Second\",\"Third\"] (max 10). Each is posted as a reply to the one before.", SortOrder = 3, SupportsBindings = true)]
    public string? ThreadParts { get; set; }
}
