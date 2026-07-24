using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using UmbHost.Automate.Teamwork.Connection;

namespace UmbHost.Automate.Teamwork.Tests;

public class TeamworkConnectionTypeTests
{
    private static TeamworkConnectionType CreateSut(HttpStatusCode status, string body)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(status, new StringContent(body));
        var factory = handler.CreateClientFactory();
        var infrastructure = new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>());
        return new TeamworkConnectionType(infrastructure, factory);
    }

    [Fact]
    public async Task ValidateAsync_returns_success_when_me_endpoint_ok()
    {
        var sut = CreateSut(HttpStatusCode.OK, "{\"person\":{\"firstName\":\"Aaron\"}}");

        var result = await sut.ValidateAsync(
            new TeamworkConnectionSettings { Site = "umbhost", ApiToken = "tok" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
    }

    [Fact]
    public async Task ValidateAsync_returns_failure_on_401()
    {
        var sut = CreateSut(HttpStatusCode.Unauthorized, "nope");

        var result = await sut.ValidateAsync(
            new TeamworkConnectionSettings { Site = "umbhost", ApiToken = "bad" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
    }

    [Fact]
    public async Task ValidateAsync_returns_failure_when_site_missing()
    {
        var sut = CreateSut(HttpStatusCode.OK, "{}");

        var result = await sut.ValidateAsync(
            new TeamworkConnectionSettings { Site = "", ApiToken = "tok" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
    }
}
