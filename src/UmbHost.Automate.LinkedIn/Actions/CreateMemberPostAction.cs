using System.Net;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Connection;
using UmbHost.Automate.LinkedIn.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Actions;

/// <summary>
/// Publishes a post as the connected member, on their own LinkedIn profile, optionally with an article link or one image.
/// </summary>
/// <remarks>
/// <para>
/// Needs <c>w_member_social</c> (the Share on LinkedIn product) to post, and <c>openid</c> and <c>profile</c> (Sign In with LinkedIn using
/// OpenID Connect) to learn the author: <c>GET /v2/userinfo</c> gives the member's <c>sub</c>, and the author is <c>urn:li:person:{sub}</c>.
/// It posts through the versioned Posts API, whose permissions list <c>w_member_social</c> ("Post, comment, and like posts on behalf of an
/// authenticated member", https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api), the same way as
/// <see cref="CreateOrganizationPostAction"/>; the image's owner is the member (Images API).
/// </para>
/// <para>
/// The first comment is best effort: the Posts API says <c>w_member_social</c> covers comments, while the Comments API lists
/// <c>w_member_social_feed</c>. A refused comment leaves the post published with a warning. There are no figures for a member's posts:
/// reading them needs <c>r_member_social</c>, which LinkedIn restricts to approved developers.
/// </para>
/// </remarks>
[Action("linkedin.createMemberPost", "Create LinkedIn Profile Post",
    Description = "Publishes a post as you, on your own LinkedIn profile.",
    Group = "Social",
    Icon = "icon-linkedin",
    ConnectionTypeAlias = "linkedin-profile")]
public sealed class CreateMemberPostAction : ActionBase<CreateMemberPostSettings, CreateMemberPostOutput>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOAuthCredentialsService _credentialsService;
    private readonly LinkedInPostPublisher _publisher;

    public CreateMemberPostAction(
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
    /// Wait between checks on an uploaded image LinkedIn is still processing, and the wait before using one whose status a member token
    /// can't read.
    /// </summary>
    internal TimeSpan ImagePollDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public override async Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var settings = context.GetSettings<CreateMemberPostSettings>();
        var content = new LinkedInPostContent(
            settings.Text, settings.LinkUrl, settings.LinkTitle, settings.LinkDescription, settings.ImageUrl, settings.ImageAltText, settings.FirstComment);
        if (LinkedInPostPublisher.Validate(content) is { } invalid)
        {
            return invalid;
        }

        var conn = context.Connection?.GetSettings<LinkedInProfileConnectionSettings>();
        if (conn?.OAuthCredentialsId is not { } credentialsId)
        {
            return ActionResult.Failed(new InvalidOperationException("A LinkedIn profile connection is required."), StepRunErrorCategory.Authentication);
        }

        var token = await _credentialsService.GetValidAccessTokenAsync(credentialsId, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("The LinkedIn token has expired (they last 60 days). Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        // Nothing has been sent yet, so a failure here is safe to retry.
        MemberLookup member;
        try
        {
            member = await LinkedInClient.UserInfoAsync(_httpClientFactory, token, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return PostOutcome.FromException("LinkedIn", ex, idempotent: true);
        }

        if (member.Status == HttpStatusCode.Unauthorized)
        {
            return ActionResult.Failed(
                new UnauthorizedAccessException("LinkedIn refused the access token: it has expired or been revoked. Re-authorise the connection."),
                StepRunErrorCategory.Authentication);
        }

        if ((int)member.Status is < 200 or > 299)
        {
            return PostOutcome.FromPreparationResponse(context, "LinkedIn", "member lookup", member.Status, member.Body);
        }

        if (member.PersonUrn is not { } author)
        {
            return ActionResult.Failed(
                new InvalidOperationException("LinkedIn didn't say who the account is, so the post has no author. Re-authorise the connection."),
                StepRunErrorCategory.ConfigurationError);
        }

        using var client = LinkedInClient.Create(_httpClientFactory, token, conn.ApiVersion);
        var published = await _publisher.PublishAsync(context, client, token, author, content, ImagePollDelay, imageStatusMayBeForbidden: true, cancellationToken);
        if (published.Failure is not null)
        {
            return published.Failure;
        }

        return Success(new CreateMemberPostOutput
        {
            PostUrn = published.PostUrn,
            PostUrl = LinkedInPostPublisher.PostUrl(published.PostUrn),
            Warning = published.Warning,
        });
    }
}
