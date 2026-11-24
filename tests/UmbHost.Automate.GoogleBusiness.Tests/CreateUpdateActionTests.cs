using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using Umbraco.Automate.OpenIddict.Credentials;
using UmbHost.Automate.GoogleBusiness.Actions;
using UmbHost.Automate.GoogleBusiness.Connection;

namespace UmbHost.Automate.GoogleBusiness.Tests;

// Mocked HTTP only: nothing here reaches Google. Requests and responses follow Google's published references:
// - LocalPost (v4): https://developers.google.com/my-business/reference/rest/v4/accounts.locations.localPosts
//   (summary, topicType STANDARD, callToAction {actionType, url}, media [{mediaFormat, sourceUrl}]; answers name and searchUrl)
// - VoiceOfMerchantState (Verifications v1): https://developers.google.com/my-business/reference/verifications/rest/v1/locations/getVoiceOfMerchantState
// - Accounts (Account Management v1): https://developers.google.com/my-business/reference/accountmanagement/rest/v1/accounts/list
// - Locations (Business Information v1): https://developers.google.com/my-business/reference/businessinformation/rest/v1/accounts.locations/list
public class CreateUpdateActionTests
{
    private const string Posts = "https://mybusiness.googleapis.com/v4/accounts/111/locations/222/localPosts";
    private const string Voice = "https://mybusinessverifications.googleapis.com/v1/locations/222/VoiceOfMerchantState";
    private static readonly Guid CredentialsId = Guid.NewGuid();
    private static readonly GoogleBusinessConnectionSettings Conn = new() { OAuthCredentialsId = CredentialsId };

    private static Mock<IOAuthCredentialsService> Credentials()
    {
        var credentials = new Mock<IOAuthCredentialsService>();
        credentials.Setup(c => c.GetValidAccessTokenAsync(CredentialsId, It.IsAny<CancellationToken>())).ReturnsAsync("ya29.tok");
        return credentials;
    }

    private static CreateUpdateAction Sut(Mock<HttpMessageHandler> handler)
        => new(ActionTestHarness.Infrastructure, handler.CreateClientFactory(), Credentials().Object);

    private static CreateUpdateSettings Update(Action<CreateUpdateSettings>? change = null)
    {
        var settings = new CreateUpdateSettings
        {
            LocationName = "accounts/111/locations/222",
            Summary = "Autumn menu is here",
            LinkUrl = "https://example.com/menu",
            ImageUrl = "https://example.com/media/menu.jpg",
        };
        change?.Invoke(settings);
        return settings;
    }

    private static Mock<HttpMessageHandler> Verified(List<JsonElement> bodies, bool verified = true)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Voice).ReturnsResponse(HttpStatusCode.OK, verified
            ? """{"hasVoiceOfMerchant":true,"hasBusinessAuthority":true}"""
            : """{"hasVoiceOfMerchant":false,"verify":{"hasPendingVerification":true}}""");
        handler.SetupRequest(HttpMethod.Post, Posts).Returns(async (HttpRequestMessage r, CancellationToken _) =>
        {
            bodies.Add(JsonDocument.Parse(await r.Content!.ReadAsStringAsync()).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"name":"accounts/111/locations/222/localPosts/333","searchUrl":"https://local.google.com/place?id=1&use=posts&lpsid=333","state":"LIVE"}"""),
            };
        });
        return handler;
    }

    [Fact]
    public async Task Google_business_posts_an_update_with_a_call_to_action()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Verified(bodies)).ExecuteAsync(ActionTestHarness.Context(Update(u => u.ActionType = "BOOK"), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<CreateUpdateOutput>().Subject;
        output.PostId.Should().Be("accounts/111/locations/222/localPosts/333");
        output.PostUrl.Should().Be("https://local.google.com/place?id=1&use=posts&lpsid=333");
        var body = bodies.Should().ContainSingle().Subject;
        body.GetProperty("topicType").GetString().Should().Be("STANDARD");
        body.GetProperty("summary").GetString().Should().Be("Autumn menu is here");
        body.GetProperty("callToAction").GetProperty("actionType").GetString().Should().Be("BOOK");
        body.GetProperty("callToAction").GetProperty("url").GetString().Should().Be("https://example.com/menu");
        body.GetProperty("media")[0].GetProperty("mediaFormat").GetString().Should().Be("PHOTO");
        body.GetProperty("media")[0].GetProperty("sourceUrl").GetString().Should().Be("https://example.com/media/menu.jpg");
    }

    [Fact]
    public async Task The_button_is_learn_more_unless_another_is_chosen()
    {
        var bodies = new List<JsonElement>();

        await Sut(Verified(bodies)).ExecuteAsync(ActionTestHarness.Context(Update(u => u.ActionType = ""), Conn), default);

        bodies[0].GetProperty("callToAction").GetProperty("actionType").GetString().Should().Be("LEARN_MORE");
    }

    [Fact]
    public async Task Without_a_link_there_is_no_button_and_without_an_image_no_media()
    {
        var bodies = new List<JsonElement>();

        await Sut(Verified(bodies)).ExecuteAsync(ActionTestHarness.Context(Update(u => (u.LinkUrl, u.ImageUrl) = (null, null)), Conn), default);

        bodies[0].TryGetProperty("callToAction", out _).Should().BeFalse();
        bodies[0].TryGetProperty("media", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_call_button_carries_no_url()
    {
        var bodies = new List<JsonElement>();

        await Sut(Verified(bodies)).ExecuteAsync(ActionTestHarness.Context(Update(u => u.ActionType = "CALL"), Conn), default);

        bodies[0].GetProperty("callToAction").GetProperty("actionType").GetString().Should().Be("CALL");
        bodies[0].GetProperty("callToAction").TryGetProperty("url", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Google_business_unverified_location_is_a_definite_failure()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Verified(bodies, verified: false)).ExecuteAsync(ActionTestHarness.Context(Update(), Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().StartWith("google_business.location_unverified");
        bodies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("accounts/111/locations/222", "GET_OFFER")]
    [InlineData("locations/222", "BOOK")]
    [InlineData("accounts/111/locations/../x", "BOOK")]
    public async Task A_bad_location_or_button_is_refused_before_anything_is_sent(string location, string action)
    {
        var bodies = new List<JsonElement>();
        var handler = Verified(bodies);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Update(u => (u.LocationName, u.ActionType) = (location, action)), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.VerifyAnyRequest(Times.Never());
    }

    [Fact]
    public async Task A_summary_over_1500_characters_is_refused()
    {
        var bodies = new List<JsonElement>();

        var result = await Sut(Verified(bodies)).ExecuteAsync(ActionTestHarness.Context(Update(u => u.Summary = new string('a', 1501)), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unconfirmed_update_may_exist_and_is_not_retried()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, Voice).ReturnsResponse(HttpStatusCode.OK, """{"hasVoiceOfMerchant":true}""");
        handler.SetupRequest(HttpMethod.Post, Posts).ReturnsResponse(HttpStatusCode.ServiceUnavailable);

        var result = await Sut(handler).ExecuteAsync(ActionTestHarness.Context(Update(), Conn), default);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().Contain("may have been published");
    }

    private static GoogleBusinessConnectionType Connection(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), Credentials().Object, handler.CreateClientFactory());

    [Fact]
    public async Task Testing_the_connection_lists_the_locations_by_name_and_id()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://mybusinessaccountmanagement.googleapis.com/v1/accounts?pageSize=20")
            .ReturnsResponse(HttpStatusCode.OK, """{"accounts":[{"name":"accounts/111","accountName":"Cafe Group","type":"PERSONAL"}]}""");
        handler.SetupRequest(HttpMethod.Get, "https://mybusinessbusinessinformation.googleapis.com/v1/accounts/111/locations?readMask=name,title,storefrontAddress&pageSize=100")
            .ReturnsResponse(HttpStatusCode.OK, """{"locations":[{"name":"locations/222","title":"Cafe","storefrontAddress":{"addressLines":["1 High Street"],"locality":"Leeds","postalCode":"LS1 1AA"}},{"name":"locations/223","title":"Cafe"}]}""");

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Contain("Cafe, 1 High Street, Leeds, LS1 1AA (accounts/111/locations/222)\nCafe (accounts/111/locations/223)");
    }

    [Fact]
    public async Task A_connection_with_no_locations_warns()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://mybusinessaccountmanagement.googleapis.com/v1/accounts?pageSize=20")
            .ReturnsResponse(HttpStatusCode.OK, """{"accounts":[{"name":"accounts/111","accountName":"Cafe Group"}]}""");
        handler.SetupRequest(HttpMethod.Get, "https://mybusinessbusinessinformation.googleapis.com/v1/accounts/111/locations?readMask=name,title,storefrontAddress&pageSize=100")
            .ReturnsResponse(HttpStatusCode.OK, "{}");

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Warning);
        result.Message.Should().Be("No locations found: add your business to Google Business Profile first.");
    }
}
