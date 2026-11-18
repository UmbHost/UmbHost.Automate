using System.Net;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Configuration;
using UmbHost.Automate.LinkedIn.Http;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// LinkedIn profile connection: a member posting as themselves, through OpenIddict's LinkedIn provider registered as
/// <c>LinkedInProfile</c> (<see cref="LinkedInProfileOAuthComposer"/>). It needs its own LinkedIn app, since LinkedIn keeps the Community
/// Management API (the <c>linkedin</c> connection) alone on its app. Testing the connection names the member.
/// </summary>
[ConnectionType("linkedin-profile", "LinkedIn profile",
    Group = "Social",
    Icon = "icon-linkedin",
    Description = "Connect to LinkedIn to post as yourself, on your own profile")]
public sealed class LinkedInProfileConnectionType : OAuthConnectionTypeBase<LinkedInProfileConnectionSettings>
{
    private readonly IHttpClientFactory _httpClientFactory;

    public LinkedInProfileConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
        => _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    public override string ProviderName => LinkedInProfileOAuthComposer.ProviderName;

    /// <summary>The clock the version's age is measured by.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// Checks the token, then asks LinkedIn who it belongs to (<c>GET /v2/userinfo</c>) and names them. That call is unversioned, so the
    /// API Version is checked here only for its form and age; LinkedIn's own answer to it comes when a post is made.
    /// </summary>
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var token = await base.ValidateAsync(settings, cancellationToken);
        if (token.Status != ConnectionValidationStatus.Success || settings is not LinkedInProfileConnectionSettings { OAuthCredentialsId: { } id } profile)
        {
            return token;
        }

        var now = Clock.GetUtcNow();
        if (LinkedInApiVersion.Problem(profile.ApiVersion, now) is { } problem)
        {
            return ConnectionValidationResult.Failure(problem);
        }

        return ConnectionWarning.Add(await WhoAsync(id, cancellationToken), LinkedInApiVersion.StaleWarning(profile.ApiVersion, now));
    }

    private async Task<ConnectionValidationResult> WhoAsync(Guid id, CancellationToken cancellationToken)
    {
        var accessToken = await CredentialsService.GetValidAccessTokenAsync(id, cancellationToken);
        MemberLookup member;
        try
        {
            member = await LinkedInClient.UserInfoAsync(_httpClientFactory, accessToken!, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return ConnectionValidationResult.Failure("Couldn't reach LinkedIn to find out who the account is.", [ex.Message]);
        }

        if (member.Status == HttpStatusCode.Unauthorized)
        {
            return ConnectionValidationResult.Failure(
                "LinkedIn refused the access token (401): it has expired or been revoked. Re-authorise the connection.", [member.Body]);
        }

        if (member.Status == HttpStatusCode.Forbidden)
        {
            return ConnectionValidationResult.Failure(
                "LinkedIn won't say who the account is (403). Add the \"Sign In with LinkedIn using OpenID Connect\" product to the app, then " +
                "re-authorise the connection.",
                [member.Body]);
        }

        if ((int)member.Status is < 200 or > 299)
        {
            return ConnectionValidationResult.Failure($"LinkedIn returned {(int)member.Status} when asked who the account is.", [member.Body]);
        }

        if (member.Sub is null)
        {
            return ConnectionValidationResult.Failure("LinkedIn's answer about the account had no member id, so posts couldn't name their author.", [member.Body]);
        }

        return ConnectionValidationResult.Success(
            string.IsNullOrWhiteSpace(member.Name) ? "LinkedIn is connected." : $"LinkedIn is connected as {member.Name}.");
    }
}
