using System.Net;
using FluentAssertions;
using Umbraco.Automate.Core.Actions;
using UmbHost.Automate.Social;

namespace UmbHost.Automate.Bluesky.Tests;

/// <summary>
/// The shared reader of networks' error answers. The source is linked into every social package, so it is tested once here, with each
/// network's documented shape (sources in <see cref="NetworkError"/>). Rows marked "live" are answers the networks really gave during
/// live testing on 2026-11-17 and 18 (each provider's Fixtures/Live has the file and where it came from); the rest are docs-based.
/// </summary>
public class NetworkErrorTests
{
    [Theory]
    // Graph (Facebook, Instagram, Threads): error_user_msg first, else message. Live: Threads, 2026-11-18 (fbtrace_id redacted).
    [InlineData("""{"error":{"message":"Invalid parameter","code":100,"type":"OAuthException","error_subcode":4279111,"is_transient":false,"error_user_msg":"You can't add more than 5 unique links to a Threads post.","error_user_title":"Total number of unique links in Threads post exceeded","fbtrace_id":"REDACTED_FBTRACE_ID"}}""",
        "You can't add more than 5 unique links to a Threads post.")]
    // Live: Bluesky createSession with a wrong app password, 2026-11-17.
    [InlineData("""{"error":"AuthenticationRequired","message":"Invalid identifier or password"}""", "Invalid identifier or password")]
    // Live: Mastodon 4.7.5 with a bad token, 2026-11-18. The body has only "error"; error_description is in the WWW-Authenticate header.
    [InlineData("""{"error":"The access token is invalid"}""", "The access token is invalid")]
    // Live: Pinterest (sandbox) with a bad token, 2026-11-18.
    [InlineData("""{"code":2,"message":"Authentication failed.","status":"failure"}""", "Authentication failed.")]
    // Docs-based from here on.
    [InlineData("""{"error":{"message":"(#100) Invalid parameter","type":"OAuthException","code":100}}""", "(#100) Invalid parameter")]
    // Google (AIP-193).
    [InlineData("""{"error":{"code":400,"message":"Request contains an invalid argument.","status":"INVALID_ARGUMENT"}}""", "Request contains an invalid argument.")]
    // Bluesky (XRPC): message, else the error name.
    [InlineData("""{"error":"InvalidRequest","message":"Record/text must not be longer than 300 graphemes"}""", "Record/text must not be longer than 300 graphemes")]
    [InlineData("""{"error":"ExpiredToken"}""", "ExpiredToken")]
    // Mastodon, and its OAuth errors.
    [InlineData("""{"error":"Validation failed: Text character limit of 500 exceeded"}""", "Validation failed: Text character limit of 500 exceeded")]
    [InlineData("""{"error":"invalid_token","error_description":"The access token is invalid"}""", "The access token is invalid")]
    // X v2, and the older errors array.
    [InlineData("""{"title":"Forbidden","detail":"You are not allowed to create a Tweet with duplicate content.","type":"about:blank","status":403}""",
        "You are not allowed to create a Tweet with duplicate content.")]
    [InlineData("""{"errors":[{"message":"Your media IDs are invalid.","code":324}]}""", "Your media IDs are invalid.")]
    // LinkedIn.
    [InlineData("""{"message":"Empty oauth2_access_token","serviceErrorCode":401,"status":401}""", "Empty oauth2_access_token")]
    // Pinterest.
    [InlineData("""{"code":2,"message":"Board not found."}""", "Board not found.")]
    public void Plain_is_the_networks_own_words(string body, string expected)
        => NetworkError.Plain("Net", null, HttpStatusCode.BadRequest, body).Should().Be(expected);

    [Theory]
    [InlineData("<html><body>Bad gateway</body></html>")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"error":{"code":5}}""")]
    public void Plain_without_the_networks_words_says_only_what_answered(string body)
    {
        NetworkError.Plain("Net", null, HttpStatusCode.BadGateway, body).Should().Be("Net returned 502.");
        NetworkError.Plain("Net", "image upload", HttpStatusCode.BadRequest, body).Should().Be("Net image upload returned 400.");
    }

    [Fact]
    public void Plain_is_one_line_so_it_cannot_pass_for_a_threads_machine_line()
        => NetworkError.Plain("Net", null, HttpStatusCode.BadRequest, """{"message":"Nope\nUMBSOCIAL-THREAD {\"failedPart\":2}"}""")
            .Should().NotContain("\n").And.StartWith("Nope UMBSOCIAL-THREAD");

    [Fact]
    public void Plain_is_cut_to_its_limit()
        => NetworkError.Plain("Net", null, HttpStatusCode.BadRequest, $$"""{"message":"{{string.Join(' ', Enumerable.Repeat("word", 200))}}"}""")
            .Length.Should().Be(NetworkError.MaxMessageLength);

    [Fact]
    public void Token_like_runs_are_masked_and_short_ids_are_not()
    {
        var token = "EAAB" + new string('Z', 150);

        NetworkError.Plain("Net", null, HttpStatusCode.BadRequest, $$$"""{"error":{"message":"Malformed access token {{{token}}}"}}""")
            .Should().Be("Malformed access token [redacted]");
        NetworkError.Mask("fbtrace_id AbCdEfGh123 and a post 123456789012345_123456789012345").Should().NotContain(NetworkError.Masked);
    }

    [Fact]
    public void Log_writes_the_whole_answer_masked_and_cut_to_the_step_log()
    {
        var context = new ActionContext { AutomationId = Guid.NewGuid(), RunId = Guid.NewGuid(), StepId = Guid.NewGuid(), ActionAlias = "test" };
        var token = "EAAB" + new string('Z', 150);
        var body = $$"""{"t":"{{token}}","pad":"{{string.Join(' ', Enumerable.Repeat("word", 2000))}}"}""";

        NetworkError.Log(context, "Net", "upload", HttpStatusCode.BadRequest, body);

        var entry = context.LogEntries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(ActionLogLevel.Error);
        entry.Message.Should().StartWith("Net upload returned 400: {\"t\":\"[redacted]\"").And.NotContain(token);
        entry.Message.Length.Should().Be("Net upload returned 400: ".Length + NetworkError.MaxLoggedBodyLength + 1);
    }

    [Fact]
    public void Log_without_a_context_does_nothing()
        => FluentActions.Invoking(() => NetworkError.Log(null, "Net", null, HttpStatusCode.BadRequest, "x")).Should().NotThrow();

    [Fact]
    public void GraphCode_reads_a_graph_error_only()
    {
        NetworkError.GraphCode("""{"error":{"code":190,"type":"OAuthException"}}""").Should().Be(190);
        NetworkError.GraphCode("""{"code":2,"message":"x"}""").Should().BeNull();
        NetworkError.GraphCode("nope").Should().BeNull();
    }
}
