using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Contrib.HttpClient;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.GoogleBusiness.Configuration;

namespace UmbHost.Automate.GoogleBusiness.Tests;

/// <summary>
/// The call Automate makes to renew a token (<c>AuthenticateWithRefreshTokenAsync</c>), through OpenIddict's whole client pipeline with
/// Automate's real set-up and this connector's registration. Only HTTP is mocked. Automate enables the authorisation code flow only, so
/// without the connector enabling the refresh flow OpenIddict refuses before any request is sent.
/// </summary>
public class TokenRenewalTests
{
    [Fact]
    public async Task An_expired_token_is_renewed_with_its_refresh_token()
    {
        string? sent = null;
        var http = new Mock<HttpMessageHandler>();
        http.SetupRequest(HttpMethod.Get, "https://accounts.google.com/.well-known/openid-configuration")
            .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                """
                {"issuer":"https://accounts.google.com","authorization_endpoint":"https://accounts.google.com/o/oauth2/v2/auth",
                "token_endpoint":"https://oauth2.googleapis.com/token","userinfo_endpoint":"https://openidconnect.googleapis.com/v1/userinfo",
                "jwks_uri":"https://www.googleapis.com/oauth2/v3/certs","response_types_supported":["code"],
                "grant_types_supported":["authorization_code","refresh_token"],
                "token_endpoint_auth_methods_supported":["client_secret_post","client_secret_basic"]}
                """, Encoding.UTF8, "application/json"));
        http.SetupRequest(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/certs")
            .ReturnsResponse(HttpStatusCode.OK, new StringContent("""{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"1","e":"AQAB","n":"w7Zejbf1pwlp8Qzl7QTSvbH12ZGlcad4qnrmzm90sRzU7B0np9mzO2RsENQsbBLcjfHpL93oMn9M2_COs-jSgecqSJoA4MeH01WSv6servpUAVc6OjpEE0eIW8C8nVi-zXa6_dzYPRqJUcQpT-wmOo1w_i_vV17GTEUdkd2JggoT2zCh3IbvDCf_ccH2YkR6jKU0ugVpTyWSlEoaHrlIwbc3ZwUD_ScJvNnuGmNZgAdWNhhk81BbMP473yaC2567xkvf5rEN1PsiQA4neMxzn8Wz5egukajUt_tQIpj4aclofdaTUiPy3XZa4Jw8bLPBvwkA6PUWH7y2YBaBdV8yKQ"}]}""", Encoding.UTF8, "application/json"));
        http.SetupRequest(HttpMethod.Post, "https://oauth2.googleapis.com/token", async request =>
            {
                sent = await request.Content!.ReadAsStringAsync();
                return true;
            })
            .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                """{"access_token":"renewed","token_type":"bearer","expires_in":3599}""", Encoding.UTF8, "application/json"));
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Umbraco:Automate:Providers:GoogleBusiness:ClientId"] = "1234",
            ["Umbraco:Automate:Providers:GoogleBusiness:ClientSecret"] = "shh",
        }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();
        new GoogleBusinessOAuthComposer().Compose(builder.Object);
        services.AddSingleton<IHttpClientFactory>(http.CreateClientFactory());

        var result = await services.BuildServiceProvider().GetRequiredService<OpenIddictClientService>()
            .AuthenticateWithRefreshTokenAsync(new()
            {
                ProviderName = "GoogleBusiness",
                RefreshToken = "stored-refresh-token",
                DisableUserInfo = true,
            });

        result.AccessToken.Should().Be("renewed");
        result.AccessTokenExpirationDate.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(3599), TimeSpan.FromMinutes(1));
        sent.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=stored-refresh-token");
    }
}
