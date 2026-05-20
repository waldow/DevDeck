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

    public static readonly IReadOnlyList<string> ForbiddenWithoutOverride = new[]
    {
        "/",
        "/{**catch-all}",
        "/{*catch-all}",
    };

    public static bool IsReserved(string matchPath, out string reason, bool allowCatchAllRoutes = false)
    {
        if (string.IsNullOrWhiteSpace(matchPath))
        {
            reason = "Match path is required.";
            return true;
        }

        var trimmed = matchPath.Trim();

        foreach (var forbidden in ForbiddenWithoutOverride)
        {
            if (!allowCatchAllRoutes && string.Equals(trimmed, forbidden, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Catch-all route '{forbidden}' is disabled by default. Enable DevDeck:ReverseProxy:AllowCatchAllRoutes or use a specific prefix like /app/{{**catch-all}}.";
                return true;
            }
        }

        foreach (var prefix in Prefixes)
        {
            var lhs = trimmed.TrimEnd('/').ToLowerInvariant();
            var rhs = prefix.ToLowerInvariant();
            if (lhs.Equals(rhs, StringComparison.Ordinal) ||
                lhs.StartsWith(rhs + "/", StringComparison.Ordinal))
            {
                reason = $"Match path collides with reserved DevDeck prefix '{prefix}'.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }
}
