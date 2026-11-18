using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.LinkedIn.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Publishes a post as a LinkedIn company page, optionally with an article link or one image.
/// </summary>
/// <remarks>
/// Needs the <c>w_organization_social</c> scope (Community Management API) and a member who is an
/// administrator or content admin of the page. The post itself goes out through <see cref="LinkedInPostPublisher"/>, shared with
/// <see cref="CreateMemberPostAction"/>.
/// </remarks>
[Action("linkedin.createOrganizationPost", "Create LinkedIn Company Post",
    Description = "Publishes a post as a LinkedIn company page.",
    Group = "Social",
    Icon = "icon-linkedin",
    ConnectionTypeAlias = "linkedin")]
public sealed class CreateOrganizationPostAction : ActionBase<CreateOrganizationPostSettings, CreateOrganizationPostOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;
    private readonly LinkedInPostPublisher _publisher;

    public CreateOrganizationPostAction(
        ActionInfrastructure infrastructure,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HttpClientFactoryOptions> httpClientOptions,
        IOAuthCredentialsService credentialsService)
        : base(infrastructure)
    {
        _httpClientFactory = httpClientFactory;
        _credentialsService = credentialsService;
        _publisher = new LinkedInPostPublisher(new ImageFetcher(httpClientFactory, httpClientOptions));
    }

    /// <summary>
    /// Wait between checks on an uploaded image LinkedIn is still processing.
    /// </summary>
    internal TimeSpan ImagePollDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateOrganizationPostSettings>();

        if (!long.TryParse(settings.OrganizationId, out _))
        {
            return ActionResult.Failed(new ArgumentException("Organization ID must be the page's numeric id."), StepRunErrorCategory.Validation);
        }

        var content = new LinkedInPostContent(
            settings.Text, settings.LinkUrl, settings.LinkTitle, settings.LinkDescription, settings.ImageUrl, settings.ImageAltText, settings.FirstComment);
        if (LinkedInPostPublisher.Validate(content) is { } invalid)
        {
            return invalid;
        }

        var conn = context.Connection?.GetSettings<LinkedInConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A LinkedIn connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The LinkedIn token has expired (they last 60 days). Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        using var client = LinkedInClient.Create(_httpClientFactory, token, conn.ApiVersion);
        var published = await _publisher.PublishAsync(
            context, client, token, $"urn:li:organization:{settings.OrganizationId}", content, ImagePollDelay, imageStatusMayBeForbidden: false, cancellationToken);
        if (published.Failure is not null)
        {
            return published.Failure;
        }

        return Success(new CreateOrganizationPostOutput
        {
            PostUrn = published.PostUrn,
            PostUrl = LinkedInPostPublisher.PostUrl(published.PostUrn),
            Warning = published.Warning,
        });
    }
}
