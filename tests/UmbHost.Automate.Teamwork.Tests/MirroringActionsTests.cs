using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teamwork.Actions;
using UmbHost.Automate.Teamwork.Connection;

namespace UmbHost.Automate.Teamwork.Tests;

public class MirroringActionsTests
{
    private static readonly TeamworkConnectionSettings Conn = new() { Site = "umbhost", ApiToken = "tok" };

    [Fact]
    public async Task AddTaskComment_posts_to_comments_endpoint()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, r => r.RequestUri!.AbsoluteUri.EndsWith("tasks/55/comments.json"))
               .ReturnsResponse(HttpStatusCode.Created, new StringContent("{\"comment\":{\"id\":77}}"));
        var factory = handler.CreateClientFactory();

        var sut = new AddTaskCommentAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(new AddTaskCommentSettings { TaskId = "55", Body = "hi" }, Conn);

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<AddTaskCommentOutput>().Which.CommentId.Should().Be(77);
    }

    [Fact]
    public async Task AddTaskComment_fails_validation_when_body_missing()
    {
        var sut = new AddTaskCommentAction(ActionTestHarness.Infrastructure, new Mock<HttpMessageHandler>().CreateClientFactory());
        var result = await sut.ExecuteAsync(ActionTestHarness.Context(new AddTaskCommentSettings { TaskId = "55", Body = "" }, Conn), default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Theory]
    [InlineData("complete", "tasks/55/complete.json")]
    [InlineData("reopen", "tasks/55/uncomplete.json")]
    public async Task SetTaskStatus_calls_the_right_endpoint(string status, string expectedSuffix)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Put, r => r.RequestUri!.AbsoluteUri.EndsWith(expectedSuffix))
               .ReturnsResponse(HttpStatusCode.OK);
        var factory = handler.CreateClientFactory();

        var sut = new SetTaskStatusAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(new SetTaskStatusSettings { TaskId = "55", Status = status }, Conn);

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        handler.VerifyRequest(HttpMethod.Put, r => r.RequestUri!.AbsoluteUri.EndsWith(expectedSuffix), Times.Once());
    }
}
