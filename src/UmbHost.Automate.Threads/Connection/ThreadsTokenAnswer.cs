using System.Text.Json;

namespace UmbHost.Automate.Threads.Connection;

/// <summary>
/// Reads the answer to Threads' long-lived token exchange and refresh: a JSON object with a non-empty string <c>access_token</c> and a
/// whole, positive <c>expires_in</c> in seconds (https://developers.facebook.com/docs/threads/get-started/long-lived-tokens). Any other
/// shape is not a token, and callers treat it as a failed exchange rather than storing or handing on whatever came back.
/// </summary>
internal static class ThreadsTokenAnswer
{
    /// <summary>The longest lifetime accepted; Threads issues 60 days, and anything past this is not a real answer.</summary>
    private static readonly long MaxExpiresInSeconds = (long)TimeSpan.FromDays(365).TotalSeconds;

    public static bool TryRead(string body, out string accessToken, out TimeSpan expiresIn)
    {
        accessToken = string.Empty;
        expiresIn = TimeSpan.Zero;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(token.GetString())
                || !root.TryGetProperty("expires_in", out var expires) || expires.ValueKind != JsonValueKind.Number
                || !expires.TryGetInt64(out var seconds) || seconds <= 0 || seconds > MaxExpiresInSeconds)
            {
                return false;
            }

            accessToken = token.GetString()!;
            expiresIn = TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
