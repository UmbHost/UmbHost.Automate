using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Connections;
using Umbraco.Automate.Core.Settings;
using UmbHost.Automate.Mastodon.Actions;
using UmbHost.Automate.Mastodon.Connection;

namespace UmbHost.Automate.Mastodon.Tests;

/// <summary>
/// What the live instance (umbracocommunity.social, Mastodon 4.7.5, 2026-11-18; Fixtures/Live) said, against what the connector assumes.
/// </summary>
public class LiveInstanceTests
{
    private static readonly MastodonConnectionSettings Conn = new() { InstanceUrl = "https://mastodon.social", AccessToken = "x" };

    [Fact]
    public void The_live_instance_has_the_limits_the_connector_and_readme_assume()
    {
        var instance = LiveFixture.Object("instance-v2");
        var configuration = instance["configuration"]!;

        instance["version"]!.GetValue<string>().Should().Be("4.7.5");
        // CreateStatusAction refuses images over Mastodon's default image_size_limit before uploading.
        configuration["media_attachments"]!["image_size_limit"]!.GetValue<long>().Should().Be(CreateStatusAction.MaxImageBytes);
        // The README: "Most instances allow 500 characters; each link counts as 23", and one image per status fits the limit of 4.
        configuration["statuses"]!["max_characters"]!.GetValue<int>().Should().Be(500);
        configuration["statuses"]!["characters_reserved_per_url"]!.GetValue<int>().Should().Be(23);
        configuration["statuses"]!["max_media_attachments"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(1);
        // The README's image types (JPEG, PNG, GIF, WebP) are all accepted.
        configuration["media_attachments"]!["supported_mime_types"]!.AsArray().Select(t => t!.GetValue<string>())
            .Should().Contain(["image/jpeg", "image/png", "image/gif", "image/webp"]);
    }

    private static MastodonConnectionType Connection(Mock<HttpMessageHandler> handler)
        => new(new ConnectionTypeInfrastructure(Mock.Of<IEditableModelResolver>()), handler.CreateClientFactory());

    [Fact]
    public async Task Validating_with_a_bad_token_fails_on_the_live_401()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://mastodon.social/api/v1/accounts/verify_credentials")
               .ReturnsResponse(HttpStatusCode.Unauthorized, new StringContent(LiveFixture.Text("error-401-invalid-token")));

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Failure);
        result.Message.Should().Be("Mastodon rejected the token (401).");
    }

    [Fact]
    public async Task Validating_reads_the_account_handle_from_the_live_account_shape()
    {
        // verify_credentials answers a CredentialAccount: the live Account (from a live status) plus "source", which the docs add
        // (https://docs.joinmastodon.org/entities/Account/#CredentialAccount); we never read "source".
        var account = LiveFixture.Object("status-plain")["account"]!.AsObject();
        account["source"] = new JsonObject { ["privacy"] = "public", ["sensitive"] = false, ["note"] = "" };
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Get, "https://mastodon.social/api/v1/accounts/verify_credentials")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(account.ToJsonString()));

        var result = await Connection(handler).ValidateAsync(Conn, default);

        result.Status.Should().Be(ConnectionValidationStatus.Success);
        result.Message.Should().Be("Connected as @example@mastodon.social.");
    }
}
