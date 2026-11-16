using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Settings for the <see cref="CreateInstagramPostAction"/>.
/// </summary>
public sealed class CreateInstagramPostSettings
{
    [Field(Label = "Caption", Description = "Post caption. Links in Instagram captions are not clickable.", SortOrder = 0, SupportsBindings = true)]
    public string? Caption { get; set; }

    [Field(Label = "Image URL", Description = "Required. Public https URL of a JPEG; Instagram downloads it itself.", SortOrder = 1, SupportsBindings = true)]
    public string ImageUrl { get; set; } = string.Empty;

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers.", SortOrder = 2, SupportsBindings = true)]
    public string? ImageAltText { get; set; }
}
