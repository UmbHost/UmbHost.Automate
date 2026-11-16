using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.GoogleBusiness.Configuration;

namespace UmbHost.Automate.GoogleBusiness.Tests;

/// <summary>
/// Google Business Profile uses OpenIddict's Google provider under its own name, so it has its own credentials and scope and can
/// sit beside a plain Google connection.
/// </summary>
public class OAuthRegistrationTests
{
    [Fact]
    public void The_google_provider_is_registered_as_google_business_with_offline_access()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Umbraco:Automate:Providers:GoogleBusiness:ClientId"] = "abc.apps.googleusercontent.com",
            ["Umbraco:Automate:Providers:GoogleBusiness:ClientSecret"] = "shh",
        }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();

        new GoogleBusinessOAuthComposer().Compose(builder.Object);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;

        var registration = options.Registrations.Should().ContainSingle(r => r.ProviderName == "GoogleBusiness").Subject;
        registration.ClientId.Should().Be("abc.apps.googleusercontent.com");
        registration.Scopes.Should().Contain("https://www.googleapis.com/auth/business.manage");
        registration.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/googlebusiness", UriKind.Relative));
    }
}
