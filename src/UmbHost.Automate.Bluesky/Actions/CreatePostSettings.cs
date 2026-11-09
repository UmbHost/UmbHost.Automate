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

    [Field(Label = "Link URL", Description = "Optional https link shown as a card under the post (when there is no image).", SortOrder = 3, SupportsBindings = true)]
    public string? LinkUrl { get; set; }

    [Field(Label = "Link Title", Description = "The card's title.", SortOrder = 4, SupportsBindings = true)]
    public string? LinkTitle { get; set; }

    [Field(Label = "Link Description", Description = "The card's description.", SortOrder = 5, SupportsBindings = true)]
    public string? LinkDescription { get; set; }

    [Field(Label = "Link Image URL", Description = "Optional https URL of the card's thumbnail (1 MB max). If it can't be fetched, the card goes without one.", SortOrder = 6, SupportsBindings = true)]
    public string? LinkImageUrl { get; set; }

    [Field(Label = "Language", Description = "Optional language code, e.g. en.", SortOrder = 7)]
    public string? Language { get; set; }
}
