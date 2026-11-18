using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// Test connection on a Community Management-only app: no profile, so it names the company pages the account can post as. Mocked
/// only: LinkedIn is never called. Shapes from
/// https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/organization-access-control-by-role
/// (organizationAcls, roleAssignee finder) and .../organizations/organization-lookup-api (organizations/{id}, localizedName).
/// </summary>
public class ConnectionTestTests
{
    private const string Acls = "https://api.linkedin.com/rest/organizationAcls?q=roleAssignee&state=APPROVED&start=0&count=100";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static LinkedInConnectionType Sut(Mock<HttpMessageHandler> handler)
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return new LinkedInConnectionType(
            new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), credentials.Object, handler.CreateClientFactory());
    }

    [Fact]
    public async Task Names_the_pages_the_account_can_post_as()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """
            {"paging":{"count":100,"start":0,"links":[]},"elements":[
              {"state":"APPROVED","role":"ADMINISTRATOR","roleAssignee":"urn:li:person:abc","organizationTarget":"urn:li:organization:2414183"},
              {"state":"APPROVED","role":"CONTENT_ADMINISTRATOR","roleAssignee":"urn:li:person:abc","organization":"urn:li:organization:79988552"},
              {"state":"APPROVED","role":"ANALYST","roleAssignee":"urn:li:person:abc","organizationTarget":"urn:li:organization:555"},
              {"state":"APPROVED","role":"ADMINISTRATOR","roleAssignee":"urn:li:person:abc","organizationTarget":"urn:li:organizationBrand:777"}
            ]}
            """);
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizations/2414183")
            .ReturnsResponse(HttpStatusCode.OK, """{"id":2414183,"localizedName":"Northwind"}""");
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizations/79988552")
            .ReturnsResponse(HttpStatusCode.OK, """{"id":79988552,"localizedName":"FirstDemoCompany"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("LinkedIn is connected. Can post as:\nNorthwind (2414183)\nFirstDemoCompany (79988552)");
        handler.VerifyRequest(r => r.RequestUri!.AbsolutePath == "/rest/organizations/555", Times.Never());
    }

    [Fact]
    public async Task Sends_the_api_version_and_restli_headers()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """{"elements":[]}""");

        await Sut(handler).ValidateAsync(Conn, default);

        handler.VerifyRequest(HttpMethod.Get, Acls, r =>
            r.Headers.GetValues("LinkedIn-Version").Single() == "202610"
            && r.Headers.GetValues("X-Restli-Protocol-Version").Single() == "2.0.0"
            && r.Headers.Authorization!.Parameter == "tok");
    }

    [Fact]
    public async Task A_page_whose_name_LinkedIn_wont_give_is_listed_by_id()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """
            {"elements":[{"state":"APPROVED","role":"ADMINISTRATOR","organizationTarget":"urn:li:organization:2414183"}]}
            """);
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizations/2414183").ReturnsResponse(HttpStatusCode.Forbidden);

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("LinkedIn is connected. Can post as:\n2414183");
    }

    [Fact]
    public async Task No_admin_pages_warns()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """
            {"elements":[{"state":"APPROVED","role":"ANALYST","organizationTarget":"urn:li:organization:2414183"}],"paging":{"count":100,"start":0}}
            """);

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().StartWith("LinkedIn is connected, but this account isn't an Administrator of any company page");
    }

    [Fact]
    public async Task A_401_fails_and_asks_to_reauthorise()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.Unauthorized, """{"status":401,"code":"REVOKED_ACCESS_TOKEN"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Contain("401").And.Contain("Re-authorise");
        result.Details.Should().ContainSingle().Which.Should().Contain("REVOKED_ACCESS_TOKEN");
    }

    [Fact]
    public async Task A_403_warns_to_reauthorise_as_an_administrator()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.Forbidden, """{"status":403,"code":"ACCESS_DENIED"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Contain("rw_organization_admin").And.Contain("Administrator").And.NotContain("Insights");
    }

    [Fact]
    public async Task Another_error_fails_with_the_status()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.InternalServerError, "oops");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("LinkedIn returned 500 listing the pages this account can post as.");
    }

    [Fact]
    public async Task Follows_the_next_page_link()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """
            {"elements":[{"state":"APPROVED","role":"ADMINISTRATOR","organizationTarget":"urn:li:organization:1"}],
             "paging":{"links":[{"rel":"next","href":"/rest/organizationAcls?start=100"}]}}
            """);
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizationAcls?q=roleAssignee&state=APPROVED&start=100&count=100")
            .ReturnsResponse(HttpStatusCode.OK, """
            {"elements":[{"state":"APPROVED","role":"ADMINISTRATOR","organizationTarget":"urn:li:organization:2"}],"paging":{"links":[]}}
            """);
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizations/1").ReturnsResponse(HttpStatusCode.OK, """{"localizedName":"One"}""");
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/rest/organizations/2").ReturnsResponse(HttpStatusCode.OK, """{"localizedName":"Two"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Message.Should().Be("LinkedIn is connected. Can post as:\nOne (1)\nTwo (2)");
    }

    [Fact]
    public async Task Without_a_token_nothing_is_sent()
    {
        var handler = new Mock<HttpMessageHandler>();
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var sut = new LinkedInConnectionType(
            new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), credentials.Object, handler.CreateClientFactory());

        var result = await sut.ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        handler.VerifyAnyRequest(Times.Never());
    }
}
