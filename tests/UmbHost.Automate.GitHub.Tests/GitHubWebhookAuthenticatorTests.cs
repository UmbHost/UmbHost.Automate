using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Umbraco.Automate.Core.Triggers.Webhooks;
using UmbHost.Automate.GitHub.Webhooks;

namespace UmbHost.Automate.GitHub.Tests;

public class GitHubWebhookAuthenticatorTests
{
    private static string Sign(string body, string key) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private static bool Validate(string body, string headerValue, string key)
    {
        var request = new DefaultHttpContext().Request;
        if (!string.IsNullOrEmpty(headerValue))
        {
            request.Headers["X-Hub-Signature-256"] = headerValue;
        }

        var ctx = new WebhookAuthenticationContext { Request = request, Body = body };
        IWebhookAuthenticator sut = new GitHubWebhookAuthenticator();
        return sut.Validate(ctx, new GitHubWebhookAuthenticatorSettings { SigningKey = key });
    }

    [Fact]
    public void valid_signature_passes() =>
        Validate("{\"a\":1}", Sign("{\"a\":1}", "s3cret"), "s3cret").Should().BeTrue();

    [Fact]
    public void wrong_secret_fails() =>
        Validate("{\"a\":1}", Sign("{\"a\":1}", "other"), "s3cret").Should().BeFalse();

    [Fact]
    public void tampered_body_fails() =>
        Validate("{\"a\":2}", Sign("{\"a\":1}", "s3cret"), "s3cret").Should().BeFalse();

    [Fact]
    public void missing_header_fails() =>
        Validate("{\"a\":1}", "", "s3cret").Should().BeFalse();

    [Fact]
    public void requires_body_is_true() =>
        new GitHubWebhookAuthenticator().RequiresBody.Should().BeTrue();
}
