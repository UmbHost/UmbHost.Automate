using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Pinterest.Configuration;

namespace UmbHost.Automate.Pinterest.Tests;

/// <summary>
/// Pinterest has no OpenIddict web provider, so it is a manual <c>AddRegistration</c>. These run Automate's real
/// <c>AddAutomateOpenIddict</c> and check that its configurator gives that registration the configured client id, secret, scopes
/// and Automate's callback, as it does for the built-in providers.
/// </summary>
public class OAuthRegistrationTests
{
    private static (IServiceCollection Services, IUmbracoBuilder Builder) Builder(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        return (services, builder.Object);
    }

    [Fact]
    public void Manual_registration_gets_client_id_secret_scopes_and_redirect()
    {
        var (services, builder) = Builder(new()
        {
            ["Umbraco:Automate:Providers:Pinterest:ClientId"] = "1234",
            ["Umbraco:Automate:Providers:Pinterest:ClientSecret"] = "shh",
            ["Umbraco:Automate:Providers:Pinterest:Scopes:0"] = "boards:read_secret",
        });
        builder.AddAutomateOpenIddict();
        new PinterestOAuthComposer().Compose(builder);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;

        var registration = options.Registrations.Should().ContainSingle(r => r.ProviderName == "Pinterest").Subject;
        registration.ClientId.Should().Be("1234");
        registration.ClientSecret.Should().Be("shh");
        registration.Scopes.Should().Contain(["boards:read", "pins:write", "boards:read_secret"]);
        registration.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/pinterest", UriKind.Relative));
        registration.ConfigurationManager.Should().NotBeNull("OpenIddict accepted the static configuration");
        options.RedirectionEndpointUris.Should().Contain(new Uri("umbraco/automate/oauth/callback/pinterest", UriKind.Relative));
    }

    [Fact]
    public void Without_a_client_id_nothing_is_registered()
    {
        var (services, builder) = Builder(new());
        builder.AddAutomateOpenIddict();
        new PinterestOAuthComposer().Compose(builder);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;

        options.Registrations.Should().NotContain(r => r.ProviderName == "Pinterest");
    }
}
