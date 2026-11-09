using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Bluesky.Actions;

/// <summary>
/// Settings for the <see cref="CreatePostAction"/>.
/// </summary>
public sealed class CreatePostSettings
{
    [Field(Label = "Text", Description = "Post text, up to 300 characters. Links in the text are made clickable.", SortOrder = 0, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Image URL", Description = "Optional https URL of a JPEG, PNG, GIF or WebP image (2 MB max).", SortOrder = 1, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers.", SortOrder = 2, SupportsBindings = true)]
    public string? ImageAltText { get; set; }

    [Field(Label = "Language", Description = "Optional language code, e.g. en.", SortOrder = 3)]
    public string? Language { get; set; }
}
