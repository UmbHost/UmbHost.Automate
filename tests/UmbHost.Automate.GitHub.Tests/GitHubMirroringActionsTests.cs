using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.GitHub.Actions;
using UmbHost.Automate.GitHub.Connection;

namespace UmbHost.Automate.GitHub.Tests;

public class GitHubMirroringActionsTests
{
    private static GitHubConnectionSettings Conn() => new() { OAuthCredentialsId = Guid.NewGuid() };

    [Fact]
    public async Task AddIssueComment_posts_to_comments_endpoint()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, "https://api.github.com/repos/umbhost/umbpanel/issues/123/comments")
               .ReturnsResponse(HttpStatusCode.Created, new StringContent(
                   "{\"id\":999,\"html_url\":\"https://github.com/umbhost/umbpanel/issues/123#issuecomment-999\"}"));

        var sut = new AddIssueCommentAction(
            GitHubActionTestHarness.Infrastructure, handler.CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new AddIssueCommentSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", Body = "hi" }, Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<AddIssueCommentOutput>().Which.CommentId.Should().Be(999);
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("open")]
    public async Task SetIssueState_patches_state(string state)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Patch, "https://api.github.com/repos/umbhost/umbpanel/issues/123")
               .ReturnsResponse(HttpStatusCode.OK, new StringContent("{\"number\":123}"));

        var sut = new SetIssueStateAction(
            GitHubActionTestHarness.Infrastructure, handler.CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new SetIssueStateSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", State = state }, Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<SetIssueStateOutput>().Which.State.Should().Be(state);
    }

    [Fact]
    public async Task SetIssueState_fails_validation_on_bad_state()
    {
        var sut = new SetIssueStateAction(
            GitHubActionTestHarness.Infrastructure, new Mock<HttpMessageHandler>().CreateClientFactory(), GitHubActionTestHarness.Credentials("gh_tok"));
        var context = GitHubActionTestHarness.Context(
            new SetIssueStateSettings { RepositoryFullName = "umbhost/umbpanel", IssueNumber = "123", State = "banana" }, Conn());

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }
}
