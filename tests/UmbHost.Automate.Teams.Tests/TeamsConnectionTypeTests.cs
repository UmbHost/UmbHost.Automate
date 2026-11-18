using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Cms.Core.Configuration.Models;
using UmbHost.Automate.Teams.Cards;
using UmbHost.Automate.Teams.Connection;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Tests;

public class TeamsConnectionTypeTests
{
    private const string Address = "https://prod-12.westeurope.logic.azure.com:443/workflows/abc123/triggers/manual/paths/invoke?sig=SECRETSIG";

    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _reply;
        public Recorder(Func<HttpResponseMessage> reply) => _reply = reply;
        public List<(Uri? Uri, string ContentType, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.RequestUri, request.Content?.Headers.ContentType?.MediaType ?? "", body));
            return _reply();
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 9, 30, 15, TimeSpan.Zero);
    }

    private static IHttpClientFactory Factory(Recorder handler)
    {
        var services = new ServiceCollection();
        TeamsWebhookClient.Register(services);
        services.AddHttpClient(TeamsWebhookClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static (TeamsConnectionType Sut, Recorder Handler) Create(
        HttpStatusCode status = HttpStatusCode.Accepted,
        string body = "",
        string? appUrl = "https://www.northwind.example/",
        Action<HttpResponseMessage>? tweak = null)
    {
        var handler = new Recorder(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            tweak?.Invoke(response);
            return response;
        });

        var monitor = new Mock<IOptionsMonitor<WebRoutingSettings>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(new WebRoutingSettings { UmbracoApplicationUrl = appUrl });

        var sut = new TeamsConnectionType(
            new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), Factory(handler), monitor.Object, new FixedTime());
        return (sut, handler);
    }

    [Fact]
    public async Task test_sends_one_test_card()
    {
        var (sut, handler) = Create();

        var result = await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = Address }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("Teams accepted the test card. Check it appeared in the channel or chat.");
        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.Uri!.AbsoluteUri.Should().Be(Address.Replace(":443", ""));
        request.ContentType.Should().Be("application/json");
        var expected = TeamsCard.Message(
            "This is a test from UmbSocial on www.northwind.example. Posts sent to this connection will appear here. Sent 2026-11-18 09:30:15Z.",
            null, null, null, null, "UmbSocial test");
        request.Body.Should().Equal(TeamsCard.ToWireBytes(expected));
    }

    [Fact]
    public async Task test_falls_back_to_this_site_when_no_application_url()
    {
        var (sut, handler) = Create(appUrl: null);

        await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = Address }, default);

        Encoding.UTF8.GetString(handler.Requests[0].Body).Should().Contain("UmbSocial on this site.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("$Umbraco:Automate:Secrets:TeamsWebhookUrl")]
    [InlineData("http://prod-12.westeurope.logic.azure.com/workflows/abc?sig=x")]
    [InlineData("https://evil.example/workflows/abc?sig=x")]
    [InlineData("https://prod-12.westeurope.logic.azure.com/workflows/abc\n?sig=x")]
    public async Task bad_or_unresolved_addresses_are_refused_without_a_request(string address)
    {
        var (sut, handler) = Create();

        var result = await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = address }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task test_failure_names_the_host_only()
    {
        var (sut, handler) = Create(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"WorkflowNotFound\"}}");

        var result = await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = Address }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        var everything = result.Message + "\n" + string.Join("\n", result.Details);
        everything.Should().Contain("404").And.Contain("prod-12.westeurope.logic.azure.com");
        everything.Should().NotContain("sig=").And.NotContain("/workflows/").And.NotContain("SECRETSIG").And.NotContain("WorkflowNotFound");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task a_redirect_is_not_followed()
    {
        var (sut, handler) = Create(HttpStatusCode.Found, tweak: r => r.Headers.Location = new Uri("https://evil.example/"));

        var result = await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = Address }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task a_200_answer_is_also_success()
    {
        var (sut, _) = Create(HttpStatusCode.OK);

        var result = await sut.ValidateAsync(new TeamsConnectionSettings { WebhookUrl = Address }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
    }

    [Fact]
    public void test_never_runs_on_save()
    {
        // Only ValidateAsync sends. Building the type, reading its schema and its settings defaults must not.
        var (sut, handler) = Create();

        _ = sut.GetSettingsSchema();
        _ = new TeamsConnectionSettings();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task the_client_refuses_an_address_off_the_allow_list_even_if_called_directly()
    {
        var handler = new Recorder(() => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = new TeamsWebhookClient(Factory(handler));

        var act = () => client.PostAsync(new Uri("https://evil.example/workflows/abc?sig=SECRETSIG"), new JsonObject(), default);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("evil.example").And.NotContain("SECRETSIG").And.NotContain("/workflows/");
        handler.Requests.Should().BeEmpty();
    }
}
