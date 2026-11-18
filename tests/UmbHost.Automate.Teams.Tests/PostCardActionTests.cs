using System.Net;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Teams.Actions;
using UmbHost.Automate.Teams.Cards;

namespace UmbHost.Automate.Teams.Tests;

public class PostCardActionTests
{
    private const string Host = "prod-12.westeurope.logic.azure.com";
    private const string MayHavePosted = "Microsoft Teams: The post may have been published: the network did not confirm the outcome. It was not retried, " +
        "to avoid a duplicate. Check the account before re-running this step.";

    private static Task<ActionResult> Run(RecordingHandler handler, PostCardSettings settings, object? connection = null)
        => PostCardTestSupport.Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(settings, connection ?? PostCardTestSupport.Connection), default);

    [Fact]
    public async Task a_post_is_sent_as_a_card()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var settings = new PostCardSettings
        {
            Text = "Hello *team*",
            ImageUrl = "https://www.example.com/a.png",
            ImageAltText = "A picture",
            LinkUrl = "https://www.example.com/post",
            LinkTitle = "Read it",
        };

        var result = await Run(handler, settings);

        result.Status.Should().Be(ActionResultStatus.Success);
        var output = result.OutputData.Should().BeOfType<PostCardOutput>().Subject;
        output.Accepted.Should().BeTrue();
        output.Note.Should().Be("Teams accepted the post. It can't confirm it appeared: if it didn't, check the Workflow's run history in Teams.");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Equal(TeamsCard.ToWireBytes(
            TeamsCard.Message("Hello *team*", "https://www.example.com/a.png", "A picture", "https://www.example.com/post", "Read it")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task text_is_required(string text)
    {
        var handler = new RecordingHandler();

        var result = await Run(handler, new PostCardSettings { Text = text });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://www.example.com/a.png", null)]
    [InlineData("ftp://www.example.com/a.png", null)]
    [InlineData("not a url", null)]
    [InlineData(null, "http://www.example.com/post")]
    [InlineData(null, "javascript:alert(1)")]
    public async Task image_and_link_must_be_https(string? image, string? link)
    {
        var handler = new RecordingHandler();

        var result = await Run(handler, new PostCardSettings { Text = "Hi", ImageUrl = image, LinkUrl = link });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task an_oversized_card_is_refused_before_sending()
    {
        var handler = new RecordingHandler();

        var result = await Run(handler, new PostCardSettings { Text = new string('x', TeamsCard.MaxBytes + 1) });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(201, null)]
    [InlineData(202, null)]
    [InlineData(204, null)]
    [InlineData(301, StepRunErrorCategory.Validation)]
    [InlineData(302, StepRunErrorCategory.Validation)]
    [InlineData(307, StepRunErrorCategory.Validation)]
    [InlineData(429, StepRunErrorCategory.RateLimiting)]
    [InlineData(400, StepRunErrorCategory.Validation)]
    [InlineData(401, StepRunErrorCategory.Validation)]
    [InlineData(403, StepRunErrorCategory.Validation)]
    [InlineData(404, StepRunErrorCategory.Validation)]
    [InlineData(500, StepRunErrorCategory.ConfigurationError)]
    [InlineData(503, StepRunErrorCategory.ConfigurationError)]
    public async Task status_mapping(int status, StepRunErrorCategory? category)
    {
        var handler = new RecordingHandler((HttpStatusCode)status);

        var result = await Run(handler, new PostCardSettings { Text = "Hi" });

        handler.Requests.Should().ContainSingle();
        if (category is null)
        {
            result.Status.Should().Be(ActionResultStatus.Success);
            return;
        }

        result.ErrorCategory.Should().Be(category);
        if (status >= 500)
        {
            result.Exception!.Message.Should().StartWith(MayHavePosted);
        }
    }

    [Fact]
    public async Task a_timeout_is_treated_like_a_server_error()
    {
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await Run(handler, new PostCardSettings { Text = "Hi" });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.ConfigurationError);
        result.Exception!.Message.Should().StartWith(MayHavePosted);
    }

    [Fact]
    public async Task a_cancelled_run_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(_ => { cts.Cancel(); throw new TaskCanceledException(null, null, cts.Token); });

        var result = await PostCardTestSupport.Sut(handler).ExecuteAsync(
            ActionTestHarness.Context(new PostCardSettings { Text = "Hi" }, PostCardTestSupport.Connection), cts.Token);

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Cancelled);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    public async Task a_deleted_or_disabled_workflow_fails_terminally_with_a_plain_reason(int status)
    {
        var handler = new RecordingHandler((HttpStatusCode)status, "{\"error\":{\"code\":\"WorkflowNotFound\"}}");

        var result = await Run(handler, new PostCardSettings { Text = "Hi" });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().StartWith("Teams refused the post: the Workflow may have been deleted or switched off.")
            .And.Contain(status.ToString()).And.Contain(Host);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task a_redirect_is_a_terminal_failure_that_says_nothing_was_sent(int status)
    {
        var handler = new RecordingHandler(r => new HttpResponseMessage((HttpStatusCode)status)
        {
            Headers = { Location = new Uri("https://evil.example/next") },
        });

        var result = await Run(handler, new PostCardSettings { Text = "Hi" });

        handler.Requests.Should().ContainSingle();
        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().StartWith("Teams answered with a redirect, which is never followed. Check the Workflow address.")
            .And.Contain(status.ToString()).And.Contain(Host)
            .And.NotContain("may have been published").And.NotContain("evil.example");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task a_refused_trigger_is_a_terminal_failure_without_sign_in_wording(int status)
    {
        var handler = new RecordingHandler((HttpStatusCode)status, "{\"error\":{\"message\":\"secret detail\"}}");

        var result = await Run(handler, new PostCardSettings { Text = "Hi" });

        result.ErrorCategory.Should().Be(StepRunErrorCategory.Validation);
        result.Exception!.Message.Should().StartWith("Teams refused the post: the Workflow's address no longer works, or its trigger isn't set to Anyone.")
            .And.Contain(status.ToString()).And.Contain(Host)
            .And.NotContain("secret detail");
    }

    [Fact]
    public async Task a_missing_or_bad_connection_is_an_authentication_failure()
    {
        var handler = new RecordingHandler();
        var settings = new PostCardSettings { Text = "Hi" };

        var none = await PostCardTestSupport.Sut(handler).ExecuteAsync(ActionTestHarness.Context(settings), default);
        var bad = await Run(handler, settings, new Connection.TeamsConnectionSettings { WebhookUrl = "https://evil.example/workflows/a?sig=x" });
        var unresolved = await Run(handler, settings, new Connection.TeamsConnectionSettings { WebhookUrl = "$Umbraco:Automate:Secrets:TeamsWebhookUrl" });

        foreach (var result in new[] { none, bad, unresolved })
        {
            result.ErrorCategory.Should().Be(StepRunErrorCategory.Authentication);
        }

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task an_error_code_is_shown_only_when_it_is_plain()
    {
        var plain = await Run(new RecordingHandler(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden.Flow\"}}"), new PostCardSettings { Text = "Hi" });
        var odd = await Run(new RecordingHandler(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"/workflows/abc?sig=SECRETSIG\"}}"), new PostCardSettings { Text = "Hi" });

        plain.Exception!.Message.Should().Contain("Forbidden.Flow").And.Contain("403").And.Contain(Host);
        odd.Exception!.Message.Should().NotContain("SECRETSIG").And.NotContain("/workflows/");
    }
}
