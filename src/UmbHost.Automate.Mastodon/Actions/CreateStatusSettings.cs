using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Mastodon.Actions;

/// <summary>
/// Settings for the <see cref="CreateStatusAction"/>.
/// </summary>
public sealed class CreateStatusSettings
{
    [Field(Label = "Text", Description = "Status text. Most instances allow 500 characters, with each link counting as 23.", SortOrder = 0, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Visibility", Description = "public, unlisted, private or direct.", SortOrder = 1)]
    public string Visibility { get; set; } = "public";

    [Field(Label = "Image URL", Description = "Optional https URL of a JPEG, PNG, GIF or WebP image.", SortOrder = 2, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers.", SortOrder = 3, SupportsBindings = true)]
    public string? ImageAltText { get; set; }

    [Field(Label = "Language", Description = "Optional ISO 639-1 language code, e.g. en.", SortOrder = 4)]
    public string? Language { get; set; }
}
