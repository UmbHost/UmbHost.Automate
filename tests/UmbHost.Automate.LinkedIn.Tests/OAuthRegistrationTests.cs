using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.LinkedIn.Configuration;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// LinkedIn is a manual OAuth 2.0 registration so it works on an app that has only the Community Management API: no <c>profile</c>,
/// no <c>openid</c>, no userinfo. These run Automate's real <c>AddAutomateOpenIddict</c> and check that its configurator still gives
/// the registration the configured client id, secret and Automate's callback, as it did for OpenIddict's built-in provider.
/// Endpoints and client_secret_post: https://learn.microsoft.com/en-us/linkedin/shared/authentication/authorization-code-flow
/// </summary>
public class OAuthRegistrationTests
{
    private static OpenIddictClientOptions Options(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();
        new LinkedInOAuthComposer().Compose(builder.Object);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;
    }

    private static OpenIddictClientRegistration Registration(string? insights = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Umbraco:Automate:Providers:LinkedIn:ClientId"] = "id",
            ["Umbraco:Automate:Providers:LinkedIn:ClientSecret"] = "secret",
        };
        if (insights is not null)
        {
            config["Umbraco:Automate:Providers:LinkedIn:Insights"] = insights;
        }

        return Options(config).Registrations.Should().ContainSingle(r => r.ProviderName == "LinkedIn").Subject;
    }

    [Fact]
    public void Registration_keeps_the_provider_name_client_and_callback()
    {
        var registration = Registration();

        registration.ProviderName.Should().Be(LinkedInOAuthComposer.ProviderName).And.Be("LinkedIn");
        registration.ClientId.Should().Be("id");
        registration.ClientSecret.Should().Be("secret");
        registration.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/linkedin", UriKind.Relative));
        registration.ConfigurationManager.Should().NotBeNull("OpenIddict accepted the static configuration");
    }

    [Fact]
    public void Registration_is_not_the_openid_connect_web_provider()
        => Registration().ProviderType.Should().BeNullOrEmpty("the built-in LinkedIn provider always asks for profile");

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void Never_asks_for_profile_or_openid(string? insights)
    {
        var scopes = Registration(insights).Scopes;

        scopes.Should().NotContain(OpenIddictConstants.Scopes.Profile);
        scopes.Should().NotContain(OpenIddictConstants.Scopes.OpenId);
    }

    /// <summary>
    /// The Insights switch only turns the share statistics reads on or off; the scopes are the same either way, so turning it on
    /// needs no re-authorisation. rw_organization_admin lists the account's pages on Test connection and reads share statistics.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("not a bool")]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" true ")]
    public void Asks_for_the_same_scopes_whatever_the_insights_switch_says(string? insights)
        => Registration(insights).Scopes.Should().BeEquivalentTo(["w_organization_social", "w_organization_social_feed", "r_organization_social", "rw_organization_admin"]);

    [Fact]
    public void Configuration_has_linkedins_endpoints_and_no_userinfo()
    {
        var configuration = Registration().Configuration!;

        configuration.AuthorizationEndpoint.Should().Be(new Uri("https://www.linkedin.com/oauth/v2/authorization"));
        configuration.TokenEndpoint.Should().Be(new Uri("https://www.linkedin.com/oauth/v2/accessToken"));
        configuration.UserInfoEndpoint.Should().BeNull();
        // OpenIddict adds "openid" to an authorisation request only when the server's scopes_supported lists it.
        configuration.ScopesSupported.Should().BeEmpty();
    }

    [Fact]
    public void Client_secret_goes_in_the_token_request_body()
        => Registration().Configuration!.TokenEndpointAuthMethodsSupported
            .Should().Equal(OpenIddictConstants.ClientAuthenticationMethods.ClientSecretPost);

    [Fact]
    public void No_pkce_because_the_standard_flow_documents_none()
        => Registration().Configuration!.CodeChallengeMethodsSupported.Should().BeEmpty();

    [Fact]
    public void Authorization_code_and_refresh_grants_with_a_query_code_response()
    {
        var configuration = Registration().Configuration!;

        configuration.GrantTypesSupported.Should().BeEquivalentTo(
            [OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken]);
        configuration.ResponseTypesSupported.Should().Equal(OpenIddictConstants.ResponseTypes.Code);
        configuration.ResponseModesSupported.Should().Equal(OpenIddictConstants.ResponseModes.Query);
    }

    [Fact]
    public void Without_a_client_id_nothing_is_registered()
        => Options(new()).Registrations.Should().NotContain(r => r.ProviderName == "LinkedIn");
}
