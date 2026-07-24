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

        var json = value as string ?? value.ToString();
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
