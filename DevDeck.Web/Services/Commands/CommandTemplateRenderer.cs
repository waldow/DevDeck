using System.Text.RegularExpressions;

namespace DevDeck.Web.Services.Commands;

public sealed class CommandTemplateRenderer
{
    private static readonly Regex PlaceholderRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);

    public RenderResult Render(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return new RenderResult(template ?? string.Empty, Array.Empty<string>());
        }

        var unknown = new List<string>();
        var rendered = PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var value) && value is not null)
            {
                return value;
            }
            if (!unknown.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                unknown.Add(key);
            }
            return match.Value;
        });

        return new RenderResult(rendered, unknown);
    }

    public static IReadOnlyDictionary<string, string?> BuildValues(int id, string name, int? port, string? workingDirectory)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id.ToString(),
            ["name"] = name,
            ["port"] = port?.ToString(),
            ["workingDirectory"] = workingDirectory,
        };
    }
}

public sealed record RenderResult(string Text, IReadOnlyList<string> UnknownPlaceholders);
