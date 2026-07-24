using System.Security.Cryptography;
using System.Text;
using Umbraco.Automate.Core.Triggers.Webhooks;

namespace UmbHost.Automate.GitHub.Webhooks;

/// <summary>
/// Authenticates GitHub webhooks by validating the <c>X-Hub-Signature-256</c> header
/// (format <c>sha256=&lt;hex&gt;</c>) as an HMAC-SHA256 of the raw body keyed on the webhook secret.
/// The built-in HMAC authenticator reads a different header name, so GitHub needs its own.
/// </summary>
[WebhookAuthenticator("github-hmac", "GitHub HMAC-SHA256 Signature",
    Description = "Validates GitHub's X-Hub-Signature-256 webhook signature.")]
public sealed class GitHubWebhookAuthenticator : WebhookAuthenticatorBase<GitHubWebhookAuthenticatorSettings>
{
    internal const string SignatureHeaderName = "X-Hub-Signature-256";

    /// <inheritdoc />
    protected override bool Validate(WebhookAuthenticationContext context, GitHubWebhookAuthenticatorSettings settings)
    {
        if (string.IsNullOrEmpty(settings.SigningKey))
        {
            return false;
        }

        var header = context.Request.Headers[SignatureHeaderName].FirstOrDefault();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedHex = header["sha256=".Length..];
        if (providedHex.Length != 64)
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(settings.SigningKey),
            Encoding.UTF8.GetBytes(context.Body ?? string.Empty));

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
