using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Teams.Actions;

/// <summary>
/// Settings for the <see cref="PostCardAction"/>.
/// </summary>
public sealed class PostCardSettings
{
    [Field(Label = "Text", Description = "The card's text, shown exactly as written (up to 4,000 characters).", SortOrder = 0, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Image URL", Description = "Optional https URL of an image. Teams loads it from that address, so it must be reachable from the internet.", SortOrder = 1, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers.", SortOrder = 2, SupportsBindings = true)]
    public string? ImageAltText { get; set; }

    [Field(Label = "Link URL", Description = "Optional https URL, shown as a button on the card.", SortOrder = 3, SupportsBindings = true)]
    public string? LinkUrl { get; set; }

    [Field(Label = "Link Title", Description = "The link button's label.", SortOrder = 4, SupportsBindings = true)]
    public string? LinkTitle { get; set; }
}
