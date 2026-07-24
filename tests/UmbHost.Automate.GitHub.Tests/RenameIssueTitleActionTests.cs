using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.GitHub.Actions;
using UmbHost.Automate.GitHub.Connection;

namespace UmbHost.Automate.GitHub.Tests;

public class RenameIssueTitleActionTests
{
    private static GitHubConnectionSettings Conn() => new() { OAuthCredentialsId = Guid.NewGuid() };

    [Fact]
    public async Task ExecuteAsync_patches_issue_title()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Patch, "https://api.github.com/repos/umbhost/umbpanel/issues/123")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent(
                   "{\"number\":123,\"html_url\":\"https://github.com/umbhost/umbpanel/issues/123\"}"));
        var factory = handler.CreateClientFactory();

        var sut = new RenameIssueTitleAction(
            GitHubActionTestHarness.Infrastructure, factory, GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new RenameIssueTitleSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", NewTitle = "9001 - Fix bug" },
            Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<RenameIssueTitleOutput>().Which.IssueNumber.Should().Be(123);
        handler.VerifyRequest(HttpMethod.Patch, "https://api.github.com/repos/umbhost/umbpanel/issues/123", Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_fails_validation_when_fields_missing()
    {
        var handler = new Mock<HttpMessageHandler>();
        var sut = new RenameIssueTitleAction(
            GitHubActionTestHarness.Infrastructure, handler.CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new RenameIssueTitleSettings { RepositoryFullName = "", IssueNumber = "123", NewTitle = "x" }, Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task ExecuteAsync_fails_auth_when_no_connection()
    {
        var handler = new Mock<HttpMessageHandler>();
        var sut = new RenameIssueTitleAction(
            GitHubActionTestHarness.Infrastructure, handler.CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new RenameIssueTitleSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", NewTitle = "x" });

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }

    [Fact]
    public async Task ExecuteAsync_fails_invalidresponse_on_api_error()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.NotFound, new StringContent("nope"));
        var sut = new RenameIssueTitleAction(
            GitHubActionTestHarness.Infrastructure, handler.CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new RenameIssueTitleSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", NewTitle = "x" }, Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }
}
