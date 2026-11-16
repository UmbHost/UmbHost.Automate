using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.LinkedIn.Configuration;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// <c>Umbraco:Automate:Providers:LinkedIn:Insights</c> adds the share-statistics scope, <c>rw_organization_admin</c>
/// (https://learn.microsoft.com/en-us/linkedin/marketing/community-management/organizations/share-statistics), only when it is on.
/// </summary>
public class InsightsSettingTests
{
    private static OpenIddictClientRegistration Registration(string? insights)
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

        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();
        new LinkedInOAuthComposer().Compose(builder.Object);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value
            .Registrations.Single(r => r.ProviderName == "LinkedIn");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData(" true ")]
    public void insights_on_requests_the_extra_scope(string insights)
        => Registration(insights).Scopes.Should().Contain(["w_organization_social", "r_organization_social", "rw_organization_admin"]);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("not a bool")]
    public void insights_off_requests_only_the_posting_scopes(string? insights)
    {
        var scopes = Registration(insights).Scopes;

        scopes.Should().Contain(["w_organization_social", "r_organization_social"]);
        scopes.Should().NotContain("rw_organization_admin");
    }
}
