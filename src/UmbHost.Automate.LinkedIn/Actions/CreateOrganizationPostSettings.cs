using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Settings for the <see cref="CreateOrganizationPostAction"/>.
/// </summary>
public sealed class CreateOrganizationPostSettings
{
    [Field(Label = "Organization ID", Description = "The company page's numeric id (from its admin URL, linkedin.com/company/<id>/admin).", SortOrder = 0)]
    public string OrganizationId { get; set; } = string.Empty;

    [Field(Label = "Text", Description = "Post text. #hashtags are kept; other special characters are escaped for you.", SortOrder = 1, SupportsBindings = true)]
    public string Text { get; set; } = string.Empty;

    [Field(Label = "Link URL", Description = "Optional article link. LinkedIn doesn't fetch the page, so a title is needed too. Can't be combined with an image.", SortOrder = 2, SupportsBindings = true)]
    public string? LinkUrl { get; set; }

    [Field(Label = "Link Title", Description = "Title shown on the link card.", SortOrder = 3, SupportsBindings = true)]
    public string? LinkTitle { get; set; }

    [Field(Label = "Link Description", Description = "Optional description shown on the link card.", SortOrder = 4, SupportsBindings = true)]
    public string? LinkDescription { get; set; }

    [Field(Label = "Image URL", Description = "Optional https URL of a JPEG, PNG or GIF image.", SortOrder = 5, SupportsBindings = true)]
    public string? ImageUrl { get; set; }

    [Field(Label = "Image Alt Text", Description = "Describes the image for screen readers.", SortOrder = 6, SupportsBindings = true)]
    public string? ImageAltText { get; set; }
}
