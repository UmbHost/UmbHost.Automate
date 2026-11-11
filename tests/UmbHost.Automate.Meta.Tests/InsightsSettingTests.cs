using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// While <c>Umbraco:Automate:Providers:Meta:Insights</c> is on, Validate reports the insights permissions the Page token lacks, as a
/// warning, never a failure. <c>/{id}/permissions</c> answers <c>data[] {permission, status}</c> with status granted, declined or expired
/// (https://developers.facebook.com/docs/graph-api/reference/user/permissions/); reading it with a Page token on <c>/me</c> is UNVERIFIED.
/// </summary>
public class InsightsSettingTests
{
    private const string Graph = "https://graph.facebook.com/v27.0/";
    private const string Switch = "Umbraco:Automate:Providers:Meta:Insights";

    private static readonly MetaConnectionSettings Conn = new() { PageId = "123", PageAccessToken = "ptok", InstagramAccountId = "1784" };

    private static MetaConnectionType Sut(Mock<HttpMessageHandler> handler, string? insights)
        => new(
            new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()),
            handler.CreateClientFactory(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(insights is null ? [] : new Dictionary<string, string?> { [Switch] = insights })
                .Build());

    private static Mock<HttpMessageHandler> Page(bool instagram = true)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Graph + "123?fields=name,instagram_business_account").ReturnsResponse(HttpStatusCode.OK,
            instagram ? """{"name":"Acme","instagram_business_account":{"id":"1784"},"id":"123"}""" : """{"name":"Acme","id":"123"}""");
        return handler;
    }

    private static bool IsPermissions(HttpRequestMessage r) => r.RequestUri!.GetLeftPart(UriPartial.Path) == Graph + "me/permissions";

    private static string Permissions(params (string Name, string Status)[] permissions)
        => "{\"data\":[" + string.Join(",", permissions.Select(p => $"{{\"permission\":\"{p.Name}\",\"status\":\"{p.Status}\"}}")) + "]}";

    [Fact]
    public async Task meta_validate_lists_missing_insights_permissions()
    {
        var handler = Page();
        handler.SetupRequest(HttpMethod.Get, IsPermissions).ReturnsResponse(HttpStatusCode.OK, Permissions(
            ("pages_read_engagement", "granted"), ("read_insights", "declined"), ("pages_read_user_content", "granted"), ("pages_show_list", "granted")));

        var result = await Sut(handler, "true").ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Contain("Acme");
        result.Details.Should().BeEquivalentTo(["read_insights", "instagram_manage_insights"]);
    }

    [Fact]
    public async Task with_every_insights_permission_granted_validate_succeeds()
    {
        var handler = Page();
        handler.SetupRequest(HttpMethod.Get, IsPermissions).ReturnsResponse(HttpStatusCode.OK, Permissions(
            ("read_insights", "granted"), ("pages_read_engagement", "granted"), ("pages_read_user_content", "granted"), ("instagram_manage_insights", "granted")));

        var result = await Sut(handler, " TRUE ").ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
    }

    [Fact]
    public async Task without_an_instagram_account_its_insights_permission_isnt_needed()
    {
        var handler = Page(instagram: false);
        handler.SetupRequest(HttpMethod.Get, IsPermissions).ReturnsResponse(HttpStatusCode.OK, Permissions(
            ("read_insights", "granted"), ("pages_read_engagement", "granted"), ("pages_read_user_content", "granted")));

        var result = await Sut(handler, "true").ValidateAsync(new MetaConnectionSettings { PageId = "123", PageAccessToken = "ptok" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("nope")]
    public async Task with_insights_off_validate_checks_no_permissions(string? insights)
    {
        var handler = Page();

        var result = await Sut(handler, insights).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        handler.VerifyRequest(IsPermissions, Times.Never());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"…","type":"OAuthException","code":100}}""")]
    [InlineData(HttpStatusCode.OK, "<html>")]
    public async Task permissions_that_cant_be_read_are_a_warning_not_a_failure(HttpStatusCode status, string body)
    {
        var handler = Page();
        handler.SetupRequest(HttpMethod.Get, IsPermissions).ReturnsResponse(status, body);

        var result = await Sut(handler, "true").ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Contain("Acme");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_transport_failure_on_the_permissions_call_is_a_warning(bool timeout)
    {
        var handler = Page();
        handler.SetupRequest(HttpMethod.Get, IsPermissions)
            .ThrowsAsync(timeout ? new TaskCanceledException("timed out") : new HttpRequestException("connection reset"));

        var result = await Sut(handler, "true").ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Contain("Acme");
    }

    [Fact]
    public async Task a_cancellation_is_still_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = Page();
        handler.SetupRequest(HttpMethod.Get, IsPermissions).Returns((HttpRequestMessage _, CancellationToken ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var validate = () => Sut(handler, "true").ValidateAsync(Conn, cts.Token);

        await validate.Should().ThrowAsync<OperationCanceledException>();
    }
}
