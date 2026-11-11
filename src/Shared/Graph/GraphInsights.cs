using System.Text.Json;

namespace UmbHost.Automate.Social;

/// <summary>
/// Reads a Graph insights answer, <c>{"data":[{"name","period","values":[{"value"}]}]}</c>, as Threads
/// (https://developers.facebook.com/docs/threads/insights), Facebook (https://developers.facebook.com/documentation/pages-api/platforminsights/page.md)
/// and Instagram (https://developers.facebook.com/docs/instagram-platform/reference/instagram-media/insights) document it.
/// </summary>
internal static class GraphInsights
{
    /// <summary>
    /// Each metric's value under its own name: <c>values[0].value</c>, or <c>total_value.value</c> where Instagram sends that instead.
    /// Metrics with no numeric value are left out. Throws <see cref="JsonException"/>, <see cref="KeyNotFoundException"/> or
    /// <see cref="InvalidOperationException"/> on an answer that isn't an insights answer.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Read(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (metric.GetProperty("name").GetString() is not { } name)
            {
                continue;
            }

            JsonElement value = default;
            var found = (metric.TryGetProperty("values", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                         && list[0].TryGetProperty("value", out value))
                        || (metric.TryGetProperty("total_value", out var total) && total.ValueKind == JsonValueKind.Object
                            && total.TryGetProperty("value", out value));
            if (found && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                values[name] = number;
            }
        }

        return values;
    }
}
