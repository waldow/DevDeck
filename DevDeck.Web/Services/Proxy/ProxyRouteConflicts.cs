using System.Text;
using DevDeck.Web.Data.Entities;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DevDeck.Web.Services.Proxy;

/// <summary>
/// Detects enabled routes that endpoint routing could not tell apart. Two routes with the same
/// path template (parameter names don't matter), the same Order and overlapping Match hosts
/// match exactly the same requests with the same priority, so every such request fails with
/// an AmbiguousMatchException (HTTP 500) instead of reaching either route.
/// </summary>
public static class ProxyRouteConflicts
{
    /// <summary>
    /// The first enabled route in <paramref name="others"/> (excluding <paramref name="candidate"/>
    /// itself, by Id when it has one) that would be ambiguous with <paramref name="candidate"/>.
    /// </summary>
    public static ProxyRoute? FindConflict(ProxyRoute candidate, IEnumerable<ProxyRoute> others)
    {
        if (!candidate.Enabled)
        {
            return null;
        }

        var template = CanonicalTemplate(candidate.MatchPath);
        if (template is null)
        {
            return null;
        }

        foreach (var other in others)
        {
            if (ReferenceEquals(other, candidate) || !other.Enabled ||
                (candidate.Id != 0 && other.Id == candidate.Id))
            {
                continue;
            }

            if (other.Order == candidate.Order &&
                CanonicalTemplate(other.MatchPath) == template &&
                HostsOverlap(candidate.MatchHostsCsv, other.MatchHostsCsv))
            {
                return other;
            }
        }

        return null;
    }

    public static string Describe(ProxyRoute conflict) =>
        $"Route '{conflict.Name}' already matches the same requests (same path template, order {conflict.Order} and hosts), " +
        "so every matching request would fail as ambiguous. Give one of them a different Order (lower wins) or path.";

    /// <summary>
    /// The template as routing compares it: literals case-insensitively, parameters by shape
    /// (catch-all, optional, constraints, default) but not by name. Null when it doesn't parse.
    /// </summary>
    public static string? CanonicalTemplate(string? matchPath)
    {
        if (string.IsNullOrWhiteSpace(matchPath))
        {
            return null;
        }

        RoutePattern pattern;
        try
        {
            pattern = RoutePatternFactory.Parse(matchPath.Trim());
        }
        catch (RoutePatternException)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            sb.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        sb.Append(literal.Content.ToLowerInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        sb.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        sb.Append('{');
                        if (parameter.IsCatchAll) sb.Append('*');
                        if (parameter.IsOptional) sb.Append('?');
                        foreach (var policy in parameter.ParameterPolicies)
                        {
                            sb.Append(':').Append(policy.Content ?? policy.ParameterPolicy?.GetType().FullName);
                        }
                        if (parameter.Default is not null) sb.Append('=').Append(parameter.Default);
                        sb.Append('}');
                        break;
                }
            }
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> ParseHosts(string? hostsCsv) =>
        string.IsNullOrWhiteSpace(hostsCsv)
            ? Array.Empty<string>()
            : hostsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // A route without hosts matches any host but loses to one that names its host, so only two
    // host-less routes, or two whose host patterns can both match one request, are ambiguous.
    private static bool HostsOverlap(string? a, string? b)
    {
        var aHosts = ParseHosts(a);
        var bHosts = ParseHosts(b);
        if (aHosts.Count == 0 || bHosts.Count == 0)
        {
            return aHosts.Count == 0 && bHosts.Count == 0;
        }

        return aHosts.Any(x => bHosts.Any(y => HostPatternsOverlap(x, y)));
    }

    private static bool HostPatternsOverlap(string x, string y)
    {
        var (xHost, xPort) = SplitHost(x);
        var (yHost, yPort) = SplitHost(y);
        if (xPort is not null && yPort is not null && xPort != yPort)
        {
            return false;
        }

        return xHost == "*" || yHost == "*" || xHost == yHost ||
               (xHost.StartsWith("*.", StringComparison.Ordinal) && yHost.EndsWith(xHost[1..], StringComparison.Ordinal)) ||
               (yHost.StartsWith("*.", StringComparison.Ordinal) && xHost.EndsWith(yHost[1..], StringComparison.Ordinal));
    }

    // "host", "host:port" or "host:*" (the forms HostMatcherPolicy accepts); a null port is any.
    private static (string Host, string? Port) SplitHost(string value)
    {
        var lower = value.ToLowerInvariant();
        var colon = lower.IndexOf(':');
        if (colon < 0)
        {
            return (lower, null);
        }

        var port = lower[(colon + 1)..];
        return (lower[..colon], port == "*" ? null : port);
    }
}
