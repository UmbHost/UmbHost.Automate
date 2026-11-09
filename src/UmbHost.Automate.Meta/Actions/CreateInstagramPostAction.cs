using System.Net;
using System.Text.Json;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Meta.Connection;
using UmbHost.Automate.Meta.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Meta.Actions;

/// <summary>
/// Publishes an image post to the Instagram professional account linked to the connection's Page.
/// </summary>
/// <remarks>
/// Instagram publishes in two calls: create a media container (Instagram fetches the image), wait
/// until it is FINISHED, then publish it. Nothing is public until the publish call, so the container
/// steps are safe to retry; the publish call is not (see <see cref="PostOutcome"/>). Instagram
/// limits an account to 100 API-published posts per 24 hours.
/// </remarks>
[Action("meta.createInstagramPost", "Create Instagram Post",
    Description = "Publishes an image post to Instagram.",
    Group = "Social",
    Icon = "icon-picture",
    ConnectionTypeAlias = "meta")]
public sealed class CreateInstagramPostAction : ActionBase<CreateInstagramPostSettings, MetaPostOutput>
{
    private const string Network = "Instagram";
    private const int MaxContainerPolls = 24;

    private readonly IHttpClientFactory _httpClientFactory;

    public CreateInstagramPostAction(ActionInfrastructure infrastructure, IHttpClientFactory httpClientFactory)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Wait between container status checks. 24 checks at 5 seconds stays inside Automate's
    /// 5-minute step timeout.
    /// </summary>
    internal TimeSpan ContainerPollDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateInstagramPostSettings>();

        if (!Uri.TryCreate(settings.ImageUrl, UriKind.Absolute, out var image) || image.Scheme != Uri.UriSchemeHttps)
        {
            return ActionResult.Failed(new ArgumentException("Instagram posts need a public https image URL."), StepRunErrorCategory.Validation);
        }

        var conn = context.Connection?.GetSettings<MetaConnectionSettings>();
        if (conn is null || string.IsNullOrWhiteSpace(conn.PageAccessToken))
        {
            return ActionResult.Failed(new InvalidOperationException("A Facebook & Instagram connection is required."), StepRunErrorCategory.Authentication);
        }

        if (string.IsNullOrWhiteSpace(conn.InstagramAccountId))
        {
            return ActionResult.Failed(
                new InvalidOperationException("The connection has no Instagram Account ID. Validate the connection to find it."),
                StepRunErrorCategory.ConfigurationError);
        }

        using var client = GraphClient.Create(_httpClientFactory, conn);

        using var containerForm = GraphClient.Form(
            ("image_url", settings.ImageUrl), ("caption", settings.Caption), ("alt_text", settings.ImageAltText));
        using var containerResponse = await client.PostAsync($"{conn.InstagramAccountId}/media", containerForm, cancellationToken);
        var containerBody = await containerResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!containerResponse.IsSuccessStatusCode)
        {
            return PostOutcome.FromPreparationResponse(Network, "media container", containerResponse.StatusCode, containerBody);
        }

        string containerId;
        using (var doc = JsonDocument.Parse(containerBody))
        {
            containerId = doc.RootElement.GetProperty("id").GetString()!;
        }

        var ready = await WaitForContainerAsync(client, containerId, cancellationToken);
        if (ready is not null)
        {
            return ready;
        }

        string body;
        try
        {
            using var publishForm = GraphClient.Form(("creation_id", containerId));
            using var response = await client.PostAsync($"{conn.InstagramAccountId}/media_publish", publishForm, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PostOutcome.FromResponse(Network, response.StatusCode, body, idempotent: false);
            }
        }
        catch (Exception ex)
        {
            return PostOutcome.FromException(Network, ex, idempotent: false);
        }

        string mediaId;
        using (var doc = JsonDocument.Parse(body))
        {
            mediaId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        }

        return Success(new MetaPostOutput
        {
            PostId = mediaId,
            PostUrl = await GraphClient.TryGetFieldAsync(client, mediaId, "permalink", cancellationToken),
        });
    }

    /// <summary>
    /// Polls the container until it is FINISHED. Returns null when ready, otherwise the failure.
    /// </summary>
    private async Task<ActionResult?> WaitForContainerAsync(HttpClient client, string containerId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxContainerPolls; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ContainerPollDelay, cancellationToken);
            }

            using var response = await client.GetAsync($"{containerId}?fields=status_code", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PostOutcome.FromPreparationResponse(Network, "media container status", response.StatusCode, body);
            }

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.TryGetProperty("status_code", out var s) ? s.GetString() : null;
            switch (status)
            {
                case "FINISHED":
                    return null;
                case "ERROR" or "EXPIRED":
                    return ActionResult.Failed(
                        new InvalidOperationException($"Instagram could not process the image (container status {status}). It must be a public JPEG."),
                        StepRunErrorCategory.Validation);
            }
        }

        return ActionResult.Failed(new TimeoutException("Instagram is still processing the image."), StepRunErrorCategory.Timeout);
    }
}
