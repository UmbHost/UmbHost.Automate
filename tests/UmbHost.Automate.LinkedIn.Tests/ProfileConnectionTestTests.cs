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
/// Test connection on the profile app names the member, from <c>GET /v2/userinfo</c>
/// (https://learn.microsoft.com/en-us/linkedin/consumer/integrations/self-serve/sign-in-with-linkedin-v2). Mocked only.
/// </summary>
public class ProfileConnectionTestTests
{
    private const string UserInfo = "https://api.linkedin.com/v2/userinfo";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly LinkedInProfileConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId, ApiVersion = "202610" };

    private static LinkedInProfileConnectionType Sut(Mock<HttpMessageHandler> handler, string? token = "tok")
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return new LinkedInProfileConnectionType(
            new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), credentials.Object, handler.CreateClientFactory());
    }

    [Fact]
    public async Task Names_the_member()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.OK, """{"sub":"782bbtaQ","name":"Jane Doe"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("LinkedIn is connected as Jane Doe.");
        handler.VerifyRequest(HttpMethod.Get, UserInfo, r => r.Headers.Authorization!.Parameter == "tok");
    }

    [Fact]
    public async Task A_member_without_a_name_is_still_connected()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.OK, """{"sub":"782bbtaQ"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("LinkedIn is connected.");
    }

    [Fact]
    public async Task A_refused_token_asks_for_reauthorisation()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.Unauthorized);

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Contain("Re-authorise");
    }

    [Fact]
    public async Task An_app_without_sign_in_names_the_missing_product()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.Forbidden);

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Contain("Sign In with LinkedIn using OpenID Connect");
    }

    [Fact]
    public async Task An_answer_without_a_member_id_fails()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ReturnsResponse(HttpStatusCode.OK, """{"name":"Jane"}""");

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
    }

    [Fact]
    public async Task An_unreachable_linkedin_fails_cleanly()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, UserInfo).ThrowsAsync(new HttpRequestException("down"));

        var result = await Sut(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Contain("Couldn't reach LinkedIn");
    }

    [Fact]
    public async Task No_token_fails_before_calling_linkedin()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler, token: null).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public void Uses_the_profile_provider_and_alias()
    {
        var type = Sut(new Mock<HttpMessageHandler>());

        type.ProviderName.Should().Be("LinkedInProfile");
        typeof(LinkedInProfileConnectionType).GetCustomAttributes(typeof(ConnectionTypeAttribute), false)
            .Cast<ConnectionTypeAttribute>().Single().Alias.Should().Be("linkedin-profile");
    }
}
