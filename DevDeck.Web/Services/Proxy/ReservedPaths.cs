using Microsoft.AspNetCore.Routing.Patterns;

namespace DevDeck.Web.Services.Proxy;

public static class ReservedPaths
{
    public static readonly IReadOnlyList<string> Prefixes = new[]
    {
        "/Manage",
        "/manage",
        "/css",
        "/js",
        "/lib",
        "/images",
        "/favicon.ico",
        "/_devdeck",
    };

    public static bool IsReserved(string matchPath, out string reason, bool allowCatchAllRoutes = false)
    {
        if (string.IsNullOrWhiteSpace(matchPath))
        {
            reason = "Match path is required.";
            return true;
        }

        var trimmed = matchPath.Trim();

        RoutePattern pattern;
        try
        {
            pattern = RoutePatternFactory.Parse(trimmed);
        }
        catch (RoutePatternException ex)
        {
            reason = $"Match path is not a valid route template: {ex.Message}";
            return true;
        }

        // Judge the parsed template, not the raw string: a template with no literal text at all
        // ("/", "/{**path}", "/{x}/{**rest}") matches every path, whatever its parameters are
        // named. (Whether a route can reach /Manage is enforced per request, by
        // ReservedPathMatcherPolicy.)
        if (!allowCatchAllRoutes && !HasLiteralText(pattern))
        {
            reason = $"Catch-all route '{trimmed}' is disabled by default: with no literal path segment it matches every path. Enable DevDeck:ReverseProxy:AllowCatchAllRoutes or add a literal prefix like /app/{{**catch-all}}.";
            return true;
        }

        var literalPrefix = LeadingLiteralPath(pattern);
        foreach (var prefix in Prefixes)
        {
            if (literalPrefix.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                literalPrefix.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Match path collides with reserved DevDeck prefix '{prefix}'.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Request-time counterpart of <see cref="IsReserved"/>: true when a request path falls
    /// under a reserved DevDeck prefix and so must never be served by a proxy route.
    /// </summary>
    public static bool IsReservedRequestPath(PathString path)
    {
        foreach (var prefix in Prefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLiteralText(RoutePattern pattern) =>
        pattern.PathSegments.Any(segment => segment.Parts.Any(part => part.IsLiteral));

    // "/app/v1/{**rest}" -> "/app/v1"; empty when the first segment holds a parameter.
    // Built from the parsed segments, so a template written without its leading slash
    // ("manage/x") is still compared as "/manage/x".
    private static string LeadingLiteralPath(RoutePattern pattern)
    {
        var literal = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            if (!segment.IsSimple || segment.Parts[0] is not RoutePatternLiteralPart part)
            {
                break;
            }
            literal.Add(part.Content);
        }

        return literal.Count == 0 ? string.Empty : "/" + string.Join('/', literal);
    }
}
