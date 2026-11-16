using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Pinterest.Actions;

/// <summary>
/// Settings for the <see cref="CreatePinAction"/>.
/// </summary>
public sealed class CreatePinSettings
{
    [Field(Label = "Board ID", Description = "The numeric id of the board to pin to. Test the connection to list your boards and their ids.", SortOrder = 0)]
    public string BoardId { get; set; } = string.Empty;

    [Field(Label = "Image URL", Description = "Required. A public https URL of a JPEG or PNG image, which Pinterest fetches.", SortOrder = 1, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Title", Description = "Optional Pin title, 100 characters at most.", SortOrder = 2, SupportsBindings = true)]
    public string? Title { get; set; }

    [Field(Label = "Description", Description = "Optional Pin description, 800 characters at most.", SortOrder = 3, SupportsBindings = true)]
    public string? Description { get; set; }

    [Field(Label = "Link", Description = "Optional URL the Pin opens.", SortOrder = 4, SupportsBindings = true)]
    public string? Link { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers, 500 characters at most.", SortOrder = 5, SupportsBindings = true)]
    public string? ImageAltText { get; set; }
}
