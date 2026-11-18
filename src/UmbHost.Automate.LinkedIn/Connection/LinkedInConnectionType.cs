using System.Net;
using System.Text.Json;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.OpenIddict.ConnectionTypes;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Configuration;
using UmbHost.Automate.LinkedIn.Http;

namespace UmbHost.Automate.LinkedIn.Connection;

/// <summary>
/// LinkedIn connection type using OAuth via the OpenIddict client (<see cref="LinkedInOAuthComposer"/>). The app only has the
/// Community Management API, so there is no profile to read: testing the connection names the company pages the account can post as,
/// "Name (id)", so the organization id a channel needs can be copied from there. LinkedIn issues no refresh token to most apps, so an
/// expired connection must be re-authorised.
/// </summary>
[ConnectionType("linkedin", "LinkedIn",
    Group = "Social",
    Icon = "icon-linkedin",
    Description = "Connect to LinkedIn to post as a company page")]
public sealed class LinkedInConnectionType : OAuthConnectionTypeBase<LinkedInConnectionSettings>
{
    // organizationAcls is paged by start/count; a few pages cover any real account without an unbounded loop.
    private const int PageSize = 100;
    private const int MaxPages = 5;

    // One GET per page name: the Development tier allows no BATCH_GET calls and 100 calls per member a day
    // (https://learn.microsoft.com/en-us/linkedin/marketing/increasing-access), so only the first few are named.
    private const int MaxNamed = 10;

    // The page roles w_organization_social posts for
    // (https://learn.microsoft.com/en-us/linkedin/marketing/community-management/shares/posts-api, Permissions). That page spells the
    // content role CONTENT_ADMIN, the access control page CONTENT_ADMINISTRATOR; both are accepted.
    private static readonly HashSet<string> PostingRoles = new(StringComparer.Ordinal)
    {
        "ADMINISTRATOR", "CONTENT_ADMINISTRATOR", "CONTENT_ADMIN", "DIRECT_SPONSORED_CONTENT_POSTER",
    };

    private const string OrganizationUrnPrefix = "urn:li:organization:";

    private readonly IHttpClientFactory _httpClientFactory;

    public LinkedInConnectionType(ConnectionTypeInfrastructure infrastructure, IOAuthCredentialsService credentialsService, IHttpClientFactory httpClientFactory)
        : base(infrastructure, credentialsService)
        => _httpClientFactory = httpClientFactory;

    /// <inheritdoc />
    public override string ProviderName => LinkedInOAuthComposer.ProviderName;

    /// <summary>
    /// Checks the token, then lists the company pages the account can post as. The member's page roles come from
    /// <c>GET /rest/organizationAcls?q=roleAssignee</c> and each page's name from <c>GET /rest/organizations/{id}</c>
    /// (https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/organization-access-control-by-role and
    /// .../organizations/organization-lookup-api). Both pages list <c>rw_organization_admin</c> as the permission, which the connection
    /// always asks for. A 403 (a token from before it did, or an app without the permission) warns rather than fails.
    /// </summary>
    public override async Task<ConnectionValidationResult> ValidateAsync(object? settings, CancellationToken cancellationToken)
    {
        var token = await base.ValidateAsync(settings, cancellationToken);
        if (token.Status != ConnectionValidationStatus.Success || settings is not LinkedInConnectionSettings { OAuthCredentialsId: { } id } linkedIn)
        {
            return token;
        }

        var accessToken = await CredentialsService.GetValidAccessTokenAsync(id, cancellationToken);
        using var client = LinkedInClient.Create(_httpClientFactory, accessToken!, linkedIn.ApiVersion);

        try
        {
            var organizations = new List<string>();
            for (var page = 0; page < MaxPages; page++)
            {
                using var response = await client.GetAsync(
                    $"organizationAcls?q=roleAssignee&state=APPROVED&start={page * PageSize}&count={PageSize}", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ConnectionValidationResult.Failure(
                        "LinkedIn refused the access token (401): it has expired or been revoked. Re-authorise the connection.", [body]);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return ConnectionValidationResult.Warning(
                        "LinkedIn is connected, but it won't list the pages this account can post as (403). Listing them needs " +
                        "the rw_organization_admin permission: check the app has the Community Management API, and re-authorise " +
                        "the connection as an Administrator of the page. Meanwhile the page's id is in its admin URL, " +
                        "linkedin.com/company/<id>/admin.",
                        [body]);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ConnectionValidationResult.Failure(
                        $"LinkedIn returned {(int)response.StatusCode} listing the pages this account can post as.", [body]);
                }

                using var doc = JsonDocument.Parse(body);
                var count = 0;
                if (doc.RootElement.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var acl in elements.EnumerateArray())
                    {
                        count++;
                        var organization = PostableOrganizationId(acl);
                        if (organization is not null && !organizations.Contains(organization))
                        {
                            organizations.Add(organization);
                        }
                    }
                }

                // A next link, or a full page when LinkedIn sends no links, means there may be more.
                if (!HasNextPage(doc.RootElement) && count < PageSize)
                {
                    break;
                }
            }

            if (organizations.Count == 0)
            {
                return ConnectionValidationResult.Warning(
                    "LinkedIn is connected, but this account isn't an Administrator of any company page, so it can't post as one. " +
                    "Make it an Administrator of the page, or authorise as a member who is.");
            }

            var lines = new List<string>(organizations.Count);
            foreach (var organization in organizations)
            {
                var name = lines.Count < MaxNamed ? await NameAsync(client, organization, cancellationToken) : null;
                lines.Add(string.IsNullOrWhiteSpace(name) ? organization : $"{name} ({organization})");
            }

            return ConnectionValidationResult.Success("LinkedIn is connected. Can post as:\n" + string.Join("\n", lines));
        }
        catch (HttpRequestException ex)
        {
            return ConnectionValidationResult.Failure("Couldn't reach LinkedIn to list the pages this account can post as.", [ex.Message]);
        }
        catch (JsonException ex)
        {
            return ConnectionValidationResult.Failure("LinkedIn's list of the pages this account can post as couldn't be read.", [ex.Message]);
        }
    }

    /// <summary>
    /// The organization id of an approved posting role, or null. LinkedIn's samples name the page <c>organizationTarget</c> in newer
    /// responses and <c>organization</c> in older ones; anything that isn't an <c>urn:li:organization</c> (a brand page) is skipped.
    /// </summary>
    private static string? PostableOrganizationId(JsonElement acl)
    {
        if (!acl.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || !PostingRoles.Contains(role.GetString()!))
        {
            return null;
        }

        if (acl.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() != "APPROVED")
        {
            return null;
        }

        var urn = (acl.TryGetProperty("organizationTarget", out var target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null)
            ?? (acl.TryGetProperty("organization", out var org) && org.ValueKind == JsonValueKind.String ? org.GetString() : null);

        return urn is not null && urn.StartsWith(OrganizationUrnPrefix, StringComparison.Ordinal) && urn.Length > OrganizationUrnPrefix.Length
            ? urn[OrganizationUrnPrefix.Length..]
            : null;
    }

    private static bool HasNextPage(JsonElement root)
        => root.TryGetProperty("paging", out var paging)
           && paging.TryGetProperty("links", out var links)
           && links.ValueKind == JsonValueKind.Array
           && links.EnumerateArray().Any(l => l.TryGetProperty("rel", out var rel) && rel.GetString() == "next");

    /// <summary>The page's <c>localizedName</c>, or null when LinkedIn won't say: the id alone is still enough for a channel.</summary>
    private static async Task<string?> NameAsync(HttpClient client, string organizationId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"organizations/{Uri.EscapeDataString(organizationId)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("localizedName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }
    }
}
