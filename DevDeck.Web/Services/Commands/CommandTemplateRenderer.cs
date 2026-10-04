using System.Text.RegularExpressions;

namespace DevDeck.Web.Services.Commands;

public sealed class CommandTemplateRenderer
{
    // "{port}" but not a shell's "${PORT}": that is the shell's own variable syntax (the
    // lookup is case-insensitive, so it would otherwise be rewritten to "$5173").
    private static readonly Regex PlaceholderRegex = new(@"(?<!\$)\{(\w+)\}", RegexOptions.Compiled);

    public RenderResult Render(string? template, IReadOnlyDictionary<string, string?> values) =>
        RenderCore(template, values, quoteForArguments: false);

    /// <summary>
    /// Renders a command-line argument string. Values are spliced in so that each stays one
    /// argument: a value containing whitespace or quotes is quoted (or, when the template
    /// already wraps the placeholder in quotes, escaped) using the rules that
    /// <c>ProcessStartInfo.Arguments</c> is split by — so a working directory such as
    /// <c>C:\Users\me\OneDrive - Contoso\api</c> does not turn into four arguments.
    /// </summary>
    public RenderResult RenderArguments(string? template, IReadOnlyDictionary<string, string?> values) =>
        RenderCore(template, values, quoteForArguments: true);

    private static RenderResult RenderCore(string? template, IReadOnlyDictionary<string, string?> values, bool quoteForArguments)
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
                return quoteForArguments ? QuoteArgumentValue(template, match, value) : value;
            }
            if (!unknown.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                unknown.Add(key);
            }
            return match.Value;
        });

        return new RenderResult(rendered, unknown);
    }

    private static string QuoteArgumentValue(string template, Match match, string value)
    {
        var end = match.Index + match.Length;
        var followedByQuote = end < template.Length && template[end] == '"';
        if (CommandLine.IsInsideQuotes(template, match.Index))
        {
            // The template quotes the placeholder itself ("{workingDirectory}"): only escape.
            return CommandLine.EscapeInsideQuotes(value, followedByQuote);
        }

        // An empty value stays empty (as before) rather than becoming an empty argument.
        return value.Length == 0 ? value : CommandLine.Quote(value);
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
