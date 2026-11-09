using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Connection;
using UmbHost.Automate.Meta.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Publishes a post to a Facebook Page: a photo post when an image is set, otherwise a feed post
/// with an optional link.
/// </summary>
/// <remarks>
/// The Graph API has no idempotency key, so the create request is never retried once it may have
/// reached Meta (see <see cref="PostOutcome"/>).
/// </remarks>
[Action("meta.createFacebookPost", "Create Facebook Page Post",
    Description = "Publishes a post to a Facebook Page.",
    Group = "Social",
    Icon = "icon-facebook",
    ConnectionTypeAlias = "meta")]
public sealed class CreateFacebookPostAction : ActionBase<CreateFacebookPostSettings, MetaPostOutput>
{
    private const string Network = "Facebook";

    private readonly IHttpClientFactory _httpClientFactory;

    public CreateFacebookPostAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateFacebookPostSettings>();

        if (string.IsNullOrWhiteSpace(settings.Message)
            && string.IsNullOrWhiteSpace(settings.LinkUrl)
            && string.IsNullOrWhiteSpace(settings.ImageUrl))
        {
            return ActionResult.Failed(new ArgumentException("A message, link or image is required."), StepRunErrorCategory.Validation);
        }

        if (!string.IsNullOrWhiteSpace(settings.ImageUrl) && !IsHttps(settings.ImageUrl))
        {
            return ActionResult.Failed(new ArgumentException("Image URL must be a public https URL."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<MetaConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.PageId) || string.IsNullOrWhiteSpace(conn.PageAccessToken))
        {
            return ActionResult.Failed(new InvalidOperationException("A Facebook & Instagram connection is required."), StepRunErrorCategory.Authentication);
        }

        using var client = GraphClient.Create(_httpClientFactory, conn);

        // A photo post comes back as { id: photo id, post_id: page post id }; a feed post as { id }.
        var hasImage = !string.IsNullOrWhiteSpace(settings.ImageUrl);
        var (path, form) = hasImage
            ? ($"{conn.PageId}/photos", GraphClient.Form(("url", settings.ImageUrl), ("caption", settings.Message)))
            : ($"{conn.PageId}/feed", GraphClient.Form(("message", settings.Message), ("link", settings.LinkUrl)));

        string body;
        try
        {
            using (form)
            using (var response = await client.PostAsync(path, form, cancellationToken))
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return PostOutcome.FromResponse(Network, response.StatusCode, body, idempotent: false);
                }
            }
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        string postId;
        using (var doc = JsonDocument.Parse(body))
        {
            var idField = hasImage && doc.RootElement.TryGetProperty("post_id", out var p) ? p : doc.RootElement.GetProperty("id");
            postId = idField.GetString() ?? string.Empty;
        }

        return Success(new MetaPostOutput
        {
            PostId = postId,
            PostUrl = await GraphClient.TryGetFieldAsync(client, postId, "permalink_url", cancellationToken),
        });
    }

    private static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
