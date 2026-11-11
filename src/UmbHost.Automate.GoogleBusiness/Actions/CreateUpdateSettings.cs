using Umbraco.Automate.Core.Settings;

namespace UmbHost.Automate.GoogleBusiness.Actions;

/// <summary>
/// Settings for the <see cref="CreateUpdateAction"/>.
/// </summary>
public sealed class CreateUpdateSettings
{
    [Field(Label = "Location", Description = "The location to post to, as accounts/<id>/locations/<id>. Test the connection to list your locations.", SortOrder = 0)]
    public string LocationName { get; set; } = string.Empty;

    [Field(Label = "Summary", Description = "The update's text, 1,500 characters at most.", SortOrder = 1, SupportsBindings = true)]
    public string Summary { get; set; } = string.Empty;

    [Field(Label = "Link URL", Description = "Optional. Becomes the update's button.", SortOrder = 2, SupportsBindings = true)]
    public string? LinkUrl { get; set; }

    [Field(Label = "Button", Description = "The button's label: BOOK, ORDER, SHOP, SIGN_UP, LEARN_MORE (the default) or CALL (calls the business's phone number; no link needed).", SortOrder = 3, SupportsBindings = true)]
    public string? ActionType { get; set; }

    [Field(Label = "Image URL", Description = "Optional public https URL of a JPEG or PNG image, which Google fetches.", SortOrder = 4, SupportsBindings = true)]
    public string? ImageUrl { get; set; }
}
