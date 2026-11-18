using FluentAssertions;
using UmbHost.Automate.Teams.Http;

namespace UmbHost.Automate.Teams.Tests;

public class TeamsWebhookAddressTests
{
    private const string Logic =
        "https://prod-00.westus.logic.azure.com:443/workflows/abc/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=XYZ";

    private const string PowerPlatform =
        "https://default1234.56.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/abc/triggers/manual/paths/invoke?api-version=1&sig=XYZ";

    [Theory]
    [InlineData(Logic)]
    [InlineData(PowerPlatform)]
    public void accepts_workflows_addresses(string value)
    {
        TeamsWebhookAddress.TryParse(value, out var address, out var error).Should().BeTrue();
        address.Should().NotBeNull();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("http://prod-00.westus.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://logic.azure.com.evil.com/workflows/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://evil.com/workflows/abc?x=.logic.azure.com&sig=XYZ")]
    [InlineData("https://user@prod-00.westus.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://prod-00.westus.logic.azure.com:8443/workflows/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://127.0.0.1/workflows/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://prod-00.westus.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?api-version=1")]
    [InlineData("https://prod-00.westus.logic.azure.com/other/abc/triggers/manual/paths/invoke?sig=XYZ")]
    [InlineData("https://prod-00.westus.logic.azure.com/workflows/a bc/invoke?sig=XYZ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$Umbraco:Automate:Secrets:TeamsHook")]
    public void refuses_everything_else(string value)
    {
        TeamsWebhookAddress.TryParse(value, out var address, out var error).Should().BeFalse();
        address.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().NotContain("sig=").And.NotContain("/workflows/").And.NotContain("XYZ");
    }

    [Fact]
    public void refuses_null()
    {
        TeamsWebhookAddress.TryParse(null, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void trims_surrounding_whitespace()
    {
        TeamsWebhookAddress.TryParse(" " + Logic + "\n", out var address, out _).Should().BeTrue();
        address!.Host.Should().Be("prod-00.westus.logic.azure.com");
    }

    [Fact]
    public void host_of_never_returns_the_secret()
    {
        TeamsWebhookAddress.HostOf(Logic).Should().Be("prod-00.westus.logic.azure.com");
        TeamsWebhookAddress.HostOf("not an address sig=XYZ").Should().Be("(not a valid address)");
        TeamsWebhookAddress.HostOf(null).Should().Be("(not a valid address)");
    }
}
