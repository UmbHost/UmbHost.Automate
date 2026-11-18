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
/// The profile connection uses OpenIddict's built-in LinkedIn provider under the name <c>LinkedInProfile</c>, a second LinkedIn app, so it
/// has its own credentials and callback and sits beside the company page's hand-made <c>LinkedIn</c> registration. Runs Automate's real
/// <c>AddAutomateOpenIddict</c>, as the company page's tests do.
/// </summary>
public class ProfileOAuthRegistrationTests
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
        new LinkedInProfileOAuthComposer().Compose(builder.Object);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;
    }

    private static readonly Dictionary<string, string?> Both = new()
    {
        ["Umbraco:Automate:Providers:LinkedIn:ClientId"] = "page-app",
        ["Umbraco:Automate:Providers:LinkedIn:ClientSecret"] = "page-secret",
        ["Umbraco:Automate:Providers:LinkedInProfile:ClientId"] = "profile-app",
        ["Umbraco:Automate:Providers:LinkedInProfile:ClientSecret"] = "profile-secret",
    };

    private static OpenIddictClientRegistration Profile(OpenIddictClientOptions options)
        => options.Registrations.Should().ContainSingle(r => r.ProviderName == "LinkedInProfile").Subject;

    [Fact]
    public void Profile_registration_has_its_own_name_client_and_callback()
    {
        var registration = Profile(Options(Both));

        registration.ProviderName.Should().Be(LinkedInProfileOAuthComposer.ProviderName);
        registration.ClientId.Should().Be("profile-app");
        registration.ClientSecret.Should().Be("profile-secret");
        registration.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/linkedinprofile", UriKind.Relative));
        registration.RegistrationId.Should().Be("LinkedInProfile");
    }

    [Fact]
    public void Profile_registration_is_the_openid_connect_provider_with_share_on_linkedin()
    {
        var registration = Profile(Options(Both));

        registration.ProviderType.Should().NotBeNullOrEmpty("it is OpenIddict's built-in LinkedIn provider");
        registration.Scopes.Should().Contain(["w_member_social", OpenIddictConstants.Scopes.Profile]);
        registration.Scopes.Should().NotContain(["w_organization_social", "r_organization_social", "rw_organization_admin"]);
    }

    [Fact]
    public void Both_linkedin_apps_live_side_by_side()
    {
        var options = Options(Both);

        var page = options.Registrations.Should().ContainSingle(r => r.ProviderName == "LinkedIn").Subject;
        var profile = Profile(options);
        page.ClientId.Should().Be("page-app");
        page.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/linkedin", UriKind.Relative));
        page.Scopes.Should().NotContain(["w_member_social", OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.OpenId]);
        profile.RegistrationId.Should().NotBe(page.RegistrationId);
        options.RedirectionEndpointUris.Should().Contain([
            new Uri("umbraco/automate/oauth/callback/linkedin", UriKind.Relative),
            new Uri("umbraco/automate/oauth/callback/linkedinprofile", UriKind.Relative),
        ]);
    }

    [Fact]
    public void Without_a_profile_client_id_only_the_company_page_is_registered()
    {
        var options = Options(new()
        {
            ["Umbraco:Automate:Providers:LinkedIn:ClientId"] = "page-app",
            ["Umbraco:Automate:Providers:LinkedIn:ClientSecret"] = "page-secret",
        });

        options.Registrations.Should().ContainSingle(r => r.ProviderName == "LinkedIn");
        options.Registrations.Should().NotContain(r => r.ProviderName == "LinkedInProfile");
    }

    [Fact]
    public void The_profile_alone_registers_without_the_company_page()
    {
        var options = Options(new()
        {
            ["Umbraco:Automate:Providers:LinkedInProfile:ClientId"] = "profile-app",
            ["Umbraco:Automate:Providers:LinkedInProfile:ClientSecret"] = "profile-secret",
        });

        Profile(options).ClientId.Should().Be("profile-app");
        options.Registrations.Should().NotContain(r => r.ProviderName == "LinkedIn");
    }
}
