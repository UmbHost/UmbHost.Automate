using System.Net;
using FluentAssertions;
using Moq;
using Moq.Contrib.HttpClient;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teamwork.Actions;
using UmbHost.Automate.Teamwork.Connection;

namespace UmbHost.Automate.Teamwork.Tests;

public class CreateTaskActionTests
{
    private static readonly TeamworkConnectionSettings Conn = new() { Site = "umbhost", ApiToken = "tok" };

    [Fact]
    public async Task ExecuteAsync_posts_task_and_returns_taskId()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupRequest(HttpMethod.Post, r => r.RequestUri!.AbsoluteUri.EndsWith("tasklists/42/tasks.json"))
               .ReturnsResponse(HttpStatusCode.Created, new StringContent("{\"task\":{\"id\":9001,\"name\":\"123 - Fix bug\"}}"));
        var factory = handler.CreateClientFactory();

        var sut = new CreateTaskAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(
            new CreateTaskSettings { TasklistId = "42", Name = "123 - Fix bug" }, Conn);

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Success);
        result.OutputData.Should().BeOfType<CreateTaskOutput>().Which.TaskId.Should().Be(9001);
    }

    [Fact]
    public async Task ExecuteAsync_fails_validation_when_name_missing()
    {
        var handler = new Mock<HttpMessageHandler>();
        var factory = handler.CreateClientFactory();
        var sut = new CreateTaskAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(new CreateTaskSettings { TasklistId = "42", Name = "" }, Conn);

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
    }

    [Fact]
    public async Task ExecuteAsync_fails_auth_when_no_connection()
    {
        var handler = new Mock<HttpMessageHandler>();
        var factory = handler.CreateClientFactory();
        var sut = new CreateTaskAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(new CreateTaskSettings { TasklistId = "42", Name = "x" });

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
    }

    [Fact]
    public async Task ExecuteAsync_fails_invalidresponse_on_api_error()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.SetupAnyRequest().ReturnsResponse(HttpStatusCode.BadRequest, new StringContent("bad"));
        var factory = handler.CreateClientFactory();
        var sut = new CreateTaskAction(ActionTestHarness.Infrastructure, factory);
        var context = ActionTestHarness.Context(new CreateTaskSettings { TasklistId = "42", Name = "x" }, Conn);

        var result = await sut.ExecuteAsync(context, default);

        result.Status.Should().Be(ActionResultStatus.Failed);
        result.ErrorCategory.Should().Be(StepRunErrorCategory.InvalidResponse);
    }
}
