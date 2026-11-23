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
using UmbHost.Automate.X.Configuration;

namespace UmbHost.Automate.X.Tests;

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
        http.SetupRequest(HttpMethod.Post, "https://api.x.com/2/oauth2/token", async request =>
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
            ["Umbraco:Automate:Providers:Twitter:ClientId"] = "1234",
            ["Umbraco:Automate:Providers:Twitter:ClientSecret"] = "shh",
        }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();
        new XOAuthComposer().Compose(builder.Object);
        services.AddSingleton<IHttpClientFactory>(http.CreateClientFactory());

        var result = await services.BuildServiceProvider().GetRequiredService<OpenIddictClientService>()
            .AuthenticateWithRefreshTokenAsync(new()
            {
                ProviderName = "Twitter",
                RefreshToken = "stored-refresh-token",
                DisableUserInfo = true,
            });

        result.AccessToken.Should().Be("renewed");
        result.AccessTokenExpirationDate.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(3599), TimeSpan.FromMinutes(1));
        sent.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=stored-refresh-token");
    }
}
