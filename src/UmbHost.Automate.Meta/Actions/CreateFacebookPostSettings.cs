using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Settings for the <see cref="CreateFacebookPostAction"/>.
/// </summary>
public sealed class CreateFacebookPostSettings
{
    [Field(Label = "Message", Description = "Post text.", SortOrder = 0, SupportsBindings = true)]
    public string? Message { get; set; }

    [Field(Label = "Link URL", Description = "Optional link; Facebook shows a preview card for it. Ignored when an image is set.", SortOrder = 1, SupportsBindings = true)]
    public string? LinkUrl { get; set; }

    [Field(Label = "Image URL", Description = "Optional public https URL of an image. Facebook downloads it itself.", SortOrder = 2, SupportsBindings = true)]
    public string? ImageUrl { get; set; }
}
