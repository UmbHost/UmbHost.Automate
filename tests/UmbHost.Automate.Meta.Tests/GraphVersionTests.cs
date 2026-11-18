using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using UmbHost.Automate.Meta.Actions;
using UmbHost.Automate.Meta.Connection;

namespace UmbHost.Automate.Meta.Tests;

/// <summary>
/// The Graph API version: its form, the connector's default for an empty field, Test connection's warnings (a version far behind the
/// default, or one Meta has moved on from, per the <c>facebook-api-version</c> header,
/// https://developers.facebook.com/docs/graph-api/guides/debugging/), and Meta's answer to a deprecated version. The refusal body
/// carries the message Meta documents for it, "(#2635) You are calling a deprecated version of the Ads API. Please update to the
/// latest version: vXXX" (https://developers.facebook.com/blog/post/2024/04/16/upcoming-change-in-marketing-api-version-deprecation),
/// in Graph's error shape (https://developers.facebook.com/docs/graph-api/guides/error-handling).
/// </summary>
public class GraphVersionTests
{
    private const string Deprecated =
        """{"error":{"message":"(#2635) You are calling a deprecated version of the Ads API. Please update to the latest version: v27.0.","type":"OAuthException","code":2635,"fbtrace_id":"AbCdEf"}}""";

    // Format

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v27.0")]
    [InlineData(" v27.0 ")]
    [InlineData("v27.0/")]
    [InlineData("v3.3")]
    [InlineData("v30.1")]
    public void A_well_formed_or_empty_version_passes(string? value)
        => MetaGraphVersion.Problem(value).Should().BeNull();

    [Theory]
    [InlineData("27.0")]
    [InlineData("v27")]
    [InlineData("V27.0")]
    [InlineData("v27.0.1")]
    [InlineData("v27.x")]
    [InlineData("latest")]
    [InlineData("v２７.0")]
    public void A_malformed_version_is_refused_with_the_form_to_use(string value)
        => MetaGraphVersion.Problem(value).Should().Be("Use the form vNN.N, e.g. v27.0.");

    [Fact]
    public void The_settings_field_carries_the_check_Automate_runs_when_it_resolves_the_settings()
    {
        var field = EditableModelSchemaBuilder.Build(typeof(MetaConnectionSettings))!.Fields.Single(f => f.PropertyName == "GraphVersion");
        field.ValidationRules.Should().ContainSingle(r => r is MetaGraphVersionAttribute);

        var settings = new MetaConnectionSettings { GraphVersion = "27.0" };
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(settings.GraphVersion, new ValidationContext(settings) { MemberName = "GraphVersion" }, results)
            .Should().BeFalse();
        results.Single().ErrorMessage.Should().Be("Use the form vNN.N, e.g. v27.0.");
    }

    // Default handling

    [Fact]
    public void An_empty_field_means_the_connectors_current_default()
    {
        MetaGraphVersion.Effective(null).Should().Be(MetaGraphVersion.Default);
        MetaGraphVersion.Effective(" ").Should().Be(MetaGraphVersion.Default);
        MetaGraphVersion.Effective("v24.0").Should().Be("v24.0");
        new MetaConnectionSettings().GraphVersion.Should().BeNull();
    }

    [Fact]
    public void The_field_offers_no_default_to_save_but_shows_it_as_a_placeholder_and_isnt_required()
    {
        var field = EditableModelSchemaBuilder.Build(typeof(MetaConnectionSettings))!.Fields.Single(f => f.PropertyName == "GraphVersion");

        field.DefaultValue.Should().BeNull();
        field.IsRequired.Should().BeFalse();
        field.EditorConfig.Should().Be("""[{ "alias": "placeholder", "value": "v27.0" }]""");
        field.Description.Should().Contain("Leave empty to use the connector's current default (v27.0)");
    }

    [Fact]
    public async Task An_empty_version_calls_the_default_and_a_stored_one_is_called_as_stored()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.AbsolutePath.EndsWith("/123", StringComparison.Ordinal))
            .ReturnsResponse(HttpStatusCode.OK, """{"name":"Acme","id":"123"}""");

        await Sut(handler).ValidateAsync(new MetaConnectionSettings { PageId = "123", PageAccessToken = "ptok" }, default);
        await Sut(handler).ValidateAsync(new MetaConnectionSettings { PageId = "123", PageAccessToken = "ptok", GraphVersion = "v24.0" }, default);

        handler.VerifyRequest(r => r.RequestUri!.AbsolutePath == "/v27.0/123", Times.Once());
        handler.VerifyRequest(r => r.RequestUri!.AbsolutePath == "/v24.0/123", Times.Once());
    }

    // Staleness

    [Theory]
    [InlineData("v27.0", false)]
    [InlineData("v28.0", false)] // newer than the default
    [InlineData("v24.0", false)]
    [InlineData("v23.0", false)] // 29 May 2025: about 17 months before v27.0 (4 November 2026)
    [InlineData("v22.0", true)] // 21 January 2025: about 21 months before
    [InlineData("v20.0", true)]
    [InlineData("v19.0", true)] // older than every dated version
    [InlineData("v3.3", true)]
    public void Test_connection_warns_when_a_version_is_more_than_18_months_behind_the_default(string version, bool warns)
        => (MetaGraphVersion.StaleWarning(version) is not null).Should().Be(warns);

    [Fact]
    public void An_empty_version_is_never_behind()
        => MetaGraphVersion.StaleWarning(null).Should().BeNull();

    [Fact]
    public async Task A_stale_version_still_connects_and_adds_the_warning()
    {
        var handler = Page("v22.0", served: "v22.0");

        var result = await Sut(handler).ValidateAsync(Settings("v22.0"), default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Be(
            "Connected to the Page 'Acme'. No Instagram account is linked to it.\n" +
            "Graph API Version v22.0 is more than 18 months older than the connector's default. Meta supports each version for about two " +
            "years; move to a newer one (e.g. v27.0).");
    }

    [Fact]
    public async Task A_version_Meta_has_moved_on_from_warns_from_the_header()
    {
        var handler = Page("v24.0", served: "v25.0");

        var result = await Sut(handler).ValidateAsync(Settings("v24.0"), default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().EndWith(
            "Meta answered on v25.0, not v24.0: it no longer serves v24.0 and moves its calls on. Set a newer version in the connection's " +
            "Graph API Version.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("v27.0")]
    [InlineData("v26.0")]
    [InlineData("nonsense")]
    public void The_header_only_warns_when_a_newer_version_answered(string? served)
        => MetaGraphVersion.ServedWarning("v27.0", served).Should().BeNull();

    [Fact]
    public async Task A_current_version_connects_cleanly()
    {
        var result = await Sut(Page("v27.0", served: "v27.0")).ValidateAsync(Settings("v27.0"), default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
    }

    [Fact]
    public async Task A_malformed_version_fails_Test_connection_before_Meta_is_called()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Sut(handler).ValidateAsync(Settings("27"), default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("Use the form vNN.N, e.g. v27.0.");
        handler.VerifyAnyRequest(Times.Never());
    }

    // Deprecated version

    [Theory]
    [InlineData(400, Deprecated, true)]
    [InlineData(400, """{"error":{"message":"You are calling a deprecated version of the Graph API.","code":12}}""", true)]
    [InlineData(400, """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":190}}""", false)]
    [InlineData(400, "not json", false)]
    [InlineData(500, Deprecated, false)]
    public void Metas_refusal_of_the_version_is_recognised(int status, string body, bool refused)
        => MetaGraphVersion.IsDeprecatedAnswer((HttpStatusCode)status, body).Should().Be(refused);

    [Fact]
    public async Task Test_connection_says_the_version_is_deprecated()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.AbsolutePath == "/v20.0/123").ReturnsResponse(HttpStatusCode.BadRequest, Deprecated);

        var result = await Sut(handler).ValidateAsync(Settings("v20.0"), default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("Meta no longer supports Graph API version v20.0. Set a newer version in the connection's Graph API Version.");
    }

    [Fact]
    public async Task A_Facebook_post_refused_for_its_version_fails_as_Validation_with_the_plain_sentence()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, "https://graph.facebook.com/v20.0/123/feed").ReturnsResponse(HttpStatusCode.BadRequest, Deprecated);

        var result = await new CreateFacebookPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory()).ExecuteAsync(
            ActionTestHarness.Context(new CreateFacebookPostSettings { Message = "Hello" }, Settings("v20.0")), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Be("Meta no longer supports Graph API version v20.0. Set a newer version in the connection's Graph API Version.");
    }

    [Fact]
    public async Task An_Instagram_container_refused_for_its_version_says_the_same()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, "https://graph.facebook.com/v20.0/1784/media").ReturnsResponse(HttpStatusCode.BadRequest, Deprecated);
        var settings = Settings("v20.0");
        settings.InstagramAccountId = "1784";

        var result = await new CreateInstagramPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory()).ExecuteAsync(
            ActionTestHarness.Context(new CreateInstagramPostSettings { ImageUrl = "https://cdn.example.com/a.jpg", Caption = "Hi" }, settings), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().StartWith("Meta no longer supports Graph API version v20.0.");
    }

    [Fact]
    public async Task Metrics_refused_for_the_version_fail_the_step_rather_than_each_post()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, r => r.RequestUri!.AbsolutePath == "/v20.0/123_456").ReturnsResponse(HttpStatusCode.BadRequest, Deprecated);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Umbraco:Automate:Providers:Meta:Insights"] = "true" })
            .Build();

        var result = await new GetFacebookPostMetricsAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), config).ExecuteAsync(
            ActionTestHarness.Context(new GetFacebookPostMetricsSettings { Ids = JsonSerializer.Serialize(new[] { "123_456" }) }, Settings("v20.0")),
            default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task Other_Graph_failures_keep_their_own_mapping()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, "https://graph.facebook.com/v27.0/123/feed")
            .ReturnsResponse(HttpStatusCode.BadRequest, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""");

        var result = await new CreateFacebookPostAction(ActionTestHarness.Infrastructure, handler.CreateClientFactory()).ExecuteAsync(
            ActionTestHarness.Context(new CreateFacebookPostSettings { Message = "Hello" }, Settings(null)), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }

    private static MetaConnectionSettings Settings(string? version) => new() { PageId = "123", PageAccessToken = "ptok", GraphVersion = version };

    private static Mock<HttpMessageHandler> Page(string version, string served)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, $"https://graph.facebook.com/{version}/123?fields=name,instagram_business_account")
            .ReturnsResponse(HttpStatusCode.OK, """{"name":"Acme","id":"123"}""", configure: r => r.Headers.Add("facebook-api-version", served));
        return handler;
    }

    private static MetaConnectionType Sut(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), handler.CreateClientFactory(), new ConfigurationBuilder().Build());
}
