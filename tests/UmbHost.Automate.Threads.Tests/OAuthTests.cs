using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Contrib.HttpClient;
using OpenIddict.Client;
using Umbraco.Automate.OpenIddict.Extensions;
using Umbraco.Cms.Core.DependencyInjection;
using UmbHost.Automate.Threads.Configuration;

namespace UmbHost.Automate.Threads.Tests;

// Mocked HTTP only. Endpoints from https://developers.facebook.com/docs/threads/get-started/get-access-tokens-and-permissions and
// https://developers.facebook.com/docs/threads/get-started/long-lived-tokens (GET /access_token?grant_type=th_exchange_token, which
// answers access_token, token_type and expires_in: about 60 days).
public class OAuthTests
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
        new ThreadsOAuthComposer().Compose(builder.Object);
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictClientOptions>>().Value;
    }

    [Fact]
    public void Manual_registration_gets_client_id_secret_scopes_and_redirect()
    {
        var options = Options(new()
        {
            ["Umbraco:Automate:Providers:Threads:ClientId"] = "1234",
            ["Umbraco:Automate:Providers:Threads:ClientSecret"] = "shh",
        });

        var registration = options.Registrations.Should().ContainSingle(r => r.ProviderName == "Threads").Subject;
        registration.ClientId.Should().Be("1234");
        registration.ClientSecret.Should().Be("shh");
        registration.Scopes.Should().Contain(["threads_basic", "threads_content_publish", "threads_manage_replies"]);
        registration.RedirectUri.Should().Be(new Uri("umbraco/automate/oauth/callback/threads", UriKind.Relative));
        options.Handlers.Should().Contain(h => h.ServiceDescriptor.ServiceType == typeof(ThreadsLongLivedTokenHandler));
    }

    [Fact]
    public void Without_a_client_id_nothing_is_registered()
    {
        Options(new()).Registrations.Should().NotContain(r => r.ProviderName == "Threads");
    }

    private static OpenIddictClientEvents.ProcessAuthenticationContext Context(string provider = "Threads") => new(new OpenIddictClientTransaction())
    {
        Registration = new OpenIddictClientRegistration { ProviderName = provider, ClientSecret = "shh" },
        BackchannelAccessToken = "THShort",
    };

    [Fact]
    public async Task The_short_lived_token_is_swapped_for_a_long_lived_one_before_it_is_stored()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://graph.threads.com/access_token?grant_type=th_exchange_token&client_secret=shh&access_token=THShort")
            .ReturnsResponse(HttpStatusCode.OK, """{"access_token":"THLong","token_type":"bearer","expires_in":5183944}""");
        var context = Context();

        await new ThreadsLongLivedTokenHandler(handler.CreateClientFactory()).HandleAsync(context);

        context.IsRejected.Should().BeFalse();
        context.BackchannelAccessToken.Should().Be("THLong");
        context.BackchannelAccessTokenExpirationDate.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(5183944), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_failed_exchange_fails_the_authorisation()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid OAuth access token"}}""");
        var context = Context();

        await new ThreadsLongLivedTokenHandler(handler.CreateClientFactory()).HandleAsync(context);

        context.IsRejected.Should().BeTrue();
    }

    [Fact]
    public async Task Other_providers_are_left_alone()
    {
        var handler = new Mock<HttpMessageHandler>();
        var context = Context("LinkedIn");

        await new ThreadsLongLivedTokenHandler(handler.CreateClientFactory()).HandleAsync(context);

        context.BackchannelAccessToken.Should().Be("THShort");
        handler.VerifyAnyRequest(Times.Never());
    }
}
