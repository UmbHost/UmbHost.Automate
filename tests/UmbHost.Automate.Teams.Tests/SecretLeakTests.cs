using System.Net;
using System.Text.Json;
using FluentAssertions;
using UmbHost.Automate.Teams.Actions;

namespace UmbHost.Automate.Teams.Tests;

public class SecretLeakTests
{
    private const string Echo = "{\"error\":{\"code\":\"X\",\"message\":\"bad " + PostCardTestSupport.Address + "\"}}";

    public static TheoryData<string> Cases() => new() { "success", "302", "401", "403", "404", "500", "timeout", "network" };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task the_address_appears_nowhere(string kind)
    {
        var handler = kind switch
        {
            "success" => new RecordingHandler(HttpStatusCode.Accepted, Echo),
            "302" => new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new StringContent(Echo),
                Headers = { Location = new Uri(PostCardTestSupport.Address) },
            }),
            "401" => new RecordingHandler(HttpStatusCode.Unauthorized, Echo),
            "403" => new RecordingHandler(HttpStatusCode.Forbidden, Echo),
            "404" => new RecordingHandler(HttpStatusCode.NotFound, Echo),
            "500" => new RecordingHandler(HttpStatusCode.InternalServerError, Echo),
            "timeout" => new RecordingHandler(r => throw new TaskCanceledException("timed out calling " + r.RequestUri)),
            _ => new RecordingHandler(r => throw new HttpRequestException("failed " + r.RequestUri, new IOException(r.RequestUri!.AbsoluteUri))),
        };
        var logs = new RecordingLoggerProvider();

        var result = await PostCardTestSupport.Sut(handler, logs).ExecuteAsync(
            ActionTestHarness.Context(new PostCardSettings { Text = "Hi" }, PostCardTestSupport.Connection), default);

        handler.Requests.Should().ContainSingle();
        var everything = new List<string>(logs.Entries);
        if (result.OutputData is not null)
        {
            everything.Add(JsonSerializer.Serialize(result.OutputData, result.OutputData.GetType()));
        }

        for (var e = result.Exception; e is not null; e = e.InnerException)
        {
            everything.Add(e.Message);
            everything.Add(e.ToString());
        }

        everything.Should().NotBeEmpty();
        foreach (var text in everything)
        {
            text.Should().NotContain("SECRETSIG").And.NotContain("/workflows/").And.NotContain("sig=");
        }
    }
}
