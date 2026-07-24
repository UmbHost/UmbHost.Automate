using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Automate.Core.Bindings;

namespace UmbHost.Automate.GitHub.Bindings;

/// <summary>
/// Binding filter that reads a value out of a JSON string by a dotted/indexed path.
/// Usage: <c>${ trigger.body | jsonpath:issue.number }</c> or <c>| jsonpath:commits[0].id }</c>.
/// Returns <c>null</c> when the value is null/not JSON or the path is missing.
/// The built-in webhook trigger exposes the request body as a raw string, and the binding
/// engine can't navigate a JSON string on its own — this filter fills that gap.
/// </summary>
public sealed class JsonPathBindingFilter : IBindingFilter
{
    private static readonly Regex SegmentPattern = new(@"^(?<name>[^\[\]]*)(\[(?<idx>\d+)\])?$", RegexOptions.Compiled);

    public string Alias => "jsonpath";

    public object? Apply(object? value, string[] args)
    {
        if (value is null || args.Length == 0)
        {
            return null;
        }

        // The value can arrive as a raw JSON string (the webhook body), a System.Text.Json
        // JsonElement, or a dictionary/array rehydrated from workflow persistence. Normalise
        // all of these to a JSON string before parsing — a bare value.ToString() on a
        // dictionary yields its type name, not JSON, which is why paths silently resolved to null.
        var json = value switch
        {
            string s => s,
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.GetRawText(),
            },
            _ => JsonSerializer.Serialize(value),
        };

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var current = doc.RootElement;
            foreach (var segment in args[0].Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = SegmentPattern.Match(segment);
                if (!match.Success)
                {
                    return null;
                }

                var name = match.Groups["name"].Value;
                if (!string.IsNullOrEmpty(name))
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                    {
                        return null;
                    }
                }

                if (match.Groups["idx"].Success)
                {
                    var idx = int.Parse(match.Groups["idx"].Value);
                    if (current.ValueKind != JsonValueKind.Array || idx >= current.GetArrayLength())
                    {
                        return null;
                    }

                    current = current[idx];
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => current.GetRawText(),
            };
        }
    }
}
