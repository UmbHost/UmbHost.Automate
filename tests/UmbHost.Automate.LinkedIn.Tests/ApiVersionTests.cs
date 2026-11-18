using System.ComponentModel.DataAnnotations;
using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.LinkedIn.Actions;
using UmbHost.Automate.LinkedIn.Connection;

namespace UmbHost.Automate.LinkedIn.Tests;

/// <summary>
/// The LinkedIn-Version header: its form, the connector's default for an empty field, Test connection's age warning, and LinkedIn's
/// answer to a retired version. The refusal body is LinkedIn's own sample, "Example 5: Version header is deprecated", from
/// https://learn.microsoft.com/en-us/linkedin/marketing/error-responses (checked 2026-11-18).
/// </summary>
public class ApiVersionTests
{
    private const string Rest = "https://api.linkedin.com/rest/";
    private const string Acls = Rest + "organizationAcls?q=roleAssignee&state=APPROVED&start=0&count=100";
    private const string Retired = """{"status":426,"code":"NONEXISTENT_VERSION","message":"Requested version 20241001 is not active"}""";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly DateTimeOffset Today = FixedClock.Today.GetUtcNow();

    // Format

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("202610")]
    [InlineData(" 202610 ")]
    [InlineData("202611")] // this month
    [InlineData("202201")] // old, but well formed: Test connection warns, it isn't refused
    public void A_well_formed_or_empty_version_passes(string? value)
        => LinkedInApiVersion.Problem(value, Today).Should().BeNull();

    [Theory]
    [InlineData("2026-10")]
    [InlineData("20261")]
    [InlineData("2026100")]
    [InlineData("202613")]
    [InlineData("202600")]
    [InlineData("v202610")]
    [InlineData("Oct 26")]
    [InlineData("２０２６１０")] // full-width digits aren't \d here
    public void A_malformed_version_is_refused_with_the_form_to_use(string value)
        => LinkedInApiVersion.Problem(value, Today).Should().Be("Use the form YYYYMM, e.g. 202610.");

    [Fact]
    public void A_version_after_this_month_is_refused()
        => LinkedInApiVersion.Problem("202612", Today).Should()
            .Be("API Version 202612 is in the future: LinkedIn only has versions up to this month. Use the form YYYYMM, e.g. 202610.");

    [Fact]
    public void The_month_boundary_is_UTC()
    {
        // 23:30 on 31 October in UTC-5 is already November in UTC.
        var lateOctober = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-5));
        LinkedInApiVersion.Problem("202611", lateOctober).Should().BeNull();
        LinkedInApiVersion.Problem("202612", lateOctober).Should().NotBeNull();
    }

    [Fact]
    public void The_settings_field_carries_the_check_Automate_runs_when_it_resolves_the_settings()
    {
        foreach (var type in new[] { typeof(LinkedInConnectionSettings), typeof(LinkedInProfileConnectionSettings) })
        {
            var field = EditableModelSchemaBuilder.Build(type)!.Fields.Single(f => f.PropertyName == "ApiVersion");
            field.ValidationRules.Should().ContainSingle(r => r is LinkedInApiVersionAttribute);
        }

        var settings = new LinkedInConnectionSettings { ApiVersion = "2026-10" };
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(settings.ApiVersion, new ValidationContext(settings) { MemberName = "ApiVersion" }, results)
            .Should().BeFalse();
        results.Single().ErrorMessage.Should().Be("Use the form YYYYMM, e.g. 202610.");

        Validator.TryValidateProperty(null, new ValidationContext(settings) { MemberName = "ApiVersion" }, results = []).Should().BeTrue();
    }

    // Default handling

    [Fact]
    public void An_empty_field_means_the_connectors_current_default()
    {
        LinkedInApiVersion.Effective(null).Should().Be(LinkedInApiVersion.Default);
        LinkedInApiVersion.Effective("").Should().Be(LinkedInApiVersion.Default);
        LinkedInApiVersion.Effective(" 202501 ").Should().Be("202501");
        new LinkedInConnectionSettings().ApiVersion.Should().BeNull();
        new LinkedInProfileConnectionSettings().ApiVersion.Should().BeNull();
    }

    [Fact]
    public void The_field_offers_no_default_to_save_but_shows_it_as_a_placeholder_and_isnt_required()
    {
        foreach (var type in new[] { typeof(LinkedInConnectionSettings), typeof(LinkedInProfileConnectionSettings) })
        {
            var field = EditableModelSchemaBuilder.Build(type)!.Fields.Single(f => f.PropertyName == "ApiVersion");

            // The backoffice seeds a field with DefaultValue and saves it: null keeps a new connection on "the current default".
            field.DefaultValue.Should().BeNull();
            field.IsRequired.Should().BeFalse();
            field.EditorConfig.Should().Be("""[{ "alias": "placeholder", "value": "202610" }]""");
            field.Description.Should().Contain("Leave empty to use the connector's current default (202610)");
        }
    }

    [Fact]
    public async Task An_empty_version_sends_the_default_and_a_stored_one_is_sent_as_stored()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """{"elements":[]}""");

        await Org(handler).ValidateAsync(new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId }, default);
        await Org(handler).ValidateAsync(new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "202601" }, default);

        handler.VerifyRequest(HttpMethod.Get, Acls, r => r.Headers.GetValues("LinkedIn-Version").Single() == "202610", Times.Once());
        handler.VerifyRequest(HttpMethod.Get, Acls, r => r.Headers.GetValues("LinkedIn-Version").Single() == "202601", Times.Once());
    }

    // Staleness

    [Theory]
    [InlineData("202611", false)]
    [InlineData("202610", false)]
    [InlineData("202601", false)] // 10 months: the last month without a warning
    [InlineData("202512", true)] // 11 months
    [InlineData("202410", true)]
    public void Test_connection_warns_once_a_version_is_more_than_ten_months_old(string version, bool warns)
        => (LinkedInApiVersion.StaleWarning(version, Today) is not null).Should().Be(warns);

    [Fact]
    public void The_warning_names_the_connectors_default_while_it_is_recent()
        => LinkedInApiVersion.StaleWarning("202512", Today).Should().Be(
            "API Version 202512 is 11 months old. LinkedIn retires API versions about a year after release; move to a newer one (e.g. 202610).");

    [Fact]
    public void Once_the_default_itself_is_old_the_warning_names_last_month()
    {
        var later = FixedClock.At(2027, 10, 5).GetUtcNow();

        LinkedInApiVersion.StaleWarning(null, later).Should().Be(
            "API Version 202610 is 12 months old. LinkedIn retires API versions about a year after release; move to a newer one (e.g. 202709).");
    }

    [Fact]
    public async Task A_stale_version_still_connects_and_adds_the_warning()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse(HttpStatusCode.OK, """
            {"elements":[{"state":"APPROVED","role":"ADMINISTRATOR","organizationTarget":"urn:li:organization:2414183"}]}
            """);
        handler.SetupRequest(HttpMethod.Get, Rest + "organizations/2414183").ReturnsResponse(HttpStatusCode.OK, """{"localizedName":"Northwind"}""");

        var result = await Org(handler).ValidateAsync(
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "202512" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Be(
            "LinkedIn is connected. Can post as:\nNorthwind (2414183)\n" +
            "API Version 202512 is 11 months old. LinkedIn retires API versions about a year after release; move to a newer one (e.g. 202610).");
    }

    [Fact]
    public async Task A_stale_version_on_a_profile_connection_warns_too()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo").ReturnsResponse(HttpStatusCode.OK, """{"sub":"a","name":"Jane"}""");

        var result = await Profile(handler).ValidateAsync(
            new LinkedInProfileConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "202501" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().StartWith("LinkedIn is connected as Jane.\nAPI Version 202501 is 22 months old.");
    }

    [Fact]
    public async Task A_malformed_version_fails_Test_connection_before_LinkedIn_is_called()
    {
        var handler = new Mock<HttpMessageHandler>();

        var result = await Org(handler).ValidateAsync(
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "10/2026" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("Use the form YYYYMM, e.g. 202610.");
        handler.VerifyAnyRequest(Times.Never());
    }

    // Retired version

    [Theory]
    [InlineData(426, Retired, true)]
    [InlineData(426, "", true)]
    [InlineData(400, """{"status":400,"code":"VERSION_MISSING","message":"A version must be present. Please specify a version by adding the Linkedin-Version header."}""", true)]
    [InlineData(400, """{"status":400,"message":"Requested version 20991201 is not active"}""", true)]
    [InlineData(400, """{"status":400,"serviceErrorCode":100,"message":"Field Value validation failed in REQUEST_BODY"}""", false)]
    [InlineData(403, """{"status":403,"serviceErrorCode":100,"message":"Not enough permissions to access: partnerApiPostsExternal.CREATE.20261001"}""", false)]
    [InlineData(500, Retired, false)]
    public void LinkedIns_refusal_of_the_version_is_recognised(int status, string body, bool refused)
        => LinkedInApiVersion.IsVersionRefusal((HttpStatusCode)status, body).Should().Be(refused);

    [Fact]
    public async Task Test_connection_says_the_version_is_retired()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Acls).ReturnsResponse((HttpStatusCode)426, Retired);

        var result = await Org(handler).ValidateAsync(
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "202410" }, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("LinkedIn no longer supports API version 202410. Set a newer version in the connection's API Version.");
        result.Details.Should().ContainSingle().Which.Should().Be(Retired);
    }

    [Fact]
    public async Task A_post_refused_for_its_version_fails_as_Validation_with_the_plain_sentence()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").ReturnsResponse((HttpStatusCode)426, Retired);

        var result = await OrgPost(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "2414183", Text = "Hello" },
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId, ApiVersion = "202410" }), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Be("LinkedIn no longer supports API version 202410. Set a newer version in the connection's API Version.");
    }

    [Fact]
    public async Task A_refused_image_registration_says_the_same()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://cdn.example.com/a.png")
            .ReturnsResponse(HttpStatusCode.OK, new ByteArrayContent([1, 2]), configure: r =>
                r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"));
        handler.SetupRequest(HttpMethod.Post, Rest + "images?action=initializeUpload").ReturnsResponse((HttpStatusCode)426, Retired);

        var result = await OrgPost(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "2414183", Text = "Hello", ImageUrl = "https://cdn.example.com/a.png" },
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().Be("LinkedIn no longer supports API version 202610. Set a newer version in the connection's API Version.");
        handler.VerifyRequest(HttpMethod.Post, Rest + "posts", Times.Never());
    }

    [Fact]
    public async Task Other_failures_keep_their_own_mapping()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, Rest + "posts").ReturnsResponse(HttpStatusCode.TooManyRequests, """{"status":429,"message":"Too many requests"}""");

        var result = await OrgPost(handler).ExecuteAsync(ActionTestHarness.Context(
            new CreateOrganizationPostSettings { OrganizationId = "2414183", Text = "Hello" },
            new LinkedInConnectionSettings { OAuthCredentialsId = CredentialsId }), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.RateLimiting);
    }

    private static Mock<IOAuthCredentialsService> Credentials()
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("tok");
        return credentials;
    }

    private static LinkedInConnectionType Org(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), Credentials().Object, handler.CreateClientFactory())
        {
            Clock = FixedClock.Today,
        };

    private static LinkedInProfileConnectionType Profile(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), Credentials().Object, handler.CreateClientFactory())
        {
            Clock = FixedClock.Today,
        };

    private static CreateOrganizationPostAction OrgPost(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), SocialTestHarness.Options(), Credentials().Object)
        {
            ImagePollDelay = TimeSpan.Zero,
        };
}
