using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Threads.Configuration;

namespace UmbHost.Automate.Threads.Tests;

/// <summary>
/// <c>Umbraco:Automate:Providers:Threads:Insights</c> adds <c>threads_manage_insights</c>, "Required for making GET calls to insights
/// endpoints" (https://developers.facebook.com/docs/threads/insights), only when it is on.
/// </summary>
public class InsightsSettingTests
{
    private static OpenIddictClientRegistration Registration(string? insights)
    {
        var config = new Dictionary<string, string?>
        {
            ["Umbraco:Automate:Providers:Threads:ClientId"] = "1234",
            ["Umbraco:Automate:Providers:Threads:ClientSecret"] = "shh",
        };
        if (insights is not null)
        {
            config["Umbraco:Automate:Providers:Threads:Insights"] = insights;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        services.AddSingleton<IConfiguration>(configuration);
        var builder = new Mock<IUmbracoBuilder>();
        builder.SetupGet(b => b.Services).Returns(services);
        builder.SetupGet(b => b.Config).Returns(configuration);
        builder.Object.AddAutomateOpenIddict();
        new ThreadsOAuthComposer().Compose(builder.Object);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value
            .Registrations.Single(r => r.ProviderName == "Threads");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void insights_on_requests_the_extra_scope(string insights)
        => Registration(insights).Scopes.Should()
            .Contain(["threads_basic", "threads_content_publish", "threads_manage_replies", "threads_manage_insights"]);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("1")]
    public void insights_off_requests_only_the_posting_scopes(string? insights)
    {
        var scopes = Registration(insights).Scopes;

        scopes.Should().Contain(["threads_basic", "threads_content_publish", "threads_manage_replies"]);
        scopes.Should().NotContain("threads_manage_insights");
    }
}
