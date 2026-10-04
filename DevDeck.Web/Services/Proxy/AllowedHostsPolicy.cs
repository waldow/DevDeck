using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DevDeck.Web.Services.Proxy;

/// <summary>
/// The Host header allow-list (the standard <c>AllowedHosts</c> setting) as host filtering
/// enforces it. DevDeck has no authentication, so it must not answer requests addressed to
/// arbitrary host names: that is what stops a DNS-rebinding page (a hostile site whose name
/// re-resolves to 127.0.0.1) from driving the Manage UI from the developer's own browser.
/// </summary>
public sealed class AllowedHostsPolicy
{
    /// <summary>Loopback names DevDeck always answers to (ports are not part of the match).</summary>
    public static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "*.localhost", "127.0.0.1", "[::1]"];

    private readonly IOptionsMonitor<HostFilteringOptions> _options;

    public AllowedHostsPolicy(IOptionsMonitor<HostFilteringOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Makes sure an allow-list that doesn't already admit every host still admits the
    /// loopback names and the gateway's own host (when that is a concrete name or address
    /// rather than a bind-to-everything one).
    /// </summary>
    public static void IncludeDevDeckHosts(HostFilteringOptions options, string listenUrl)
    {
        if (options.AllowedHosts is null || options.AllowedHosts.Contains("*"))
        {
            return;
        }

        var required = LoopbackHosts.ToList();
        // Uri.Host keeps the brackets of an IPv6 literal, which is the form AllowedHosts expects.
        if (Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) && !GatewayUrlResolver.IsAnyAddress(uri.Host))
        {
            required.Add(uri.Host);
        }

        // The framework fills AllowedHosts from configuration as a fixed-size array.
        var hosts = new List<string>(options.AllowedHosts);
        foreach (var host in required)
        {
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }
        options.AllowedHosts = hosts;
    }

    /// <summary>
    /// The route Match hosts that host filtering would reject before routing ever sees the
    /// request. A host pattern ("*.example.test") counts as allowed only when the allow-list
    /// admits every name it covers.
    /// </summary>
    public IReadOnlyList<string> Blocked(IEnumerable<string> matchHosts)
    {
        var allowed = _options.CurrentValue.AllowedHosts;
        if (allowed is null || allowed.Count == 0 || allowed.Contains("*"))
        {
            return [];
        }

        var patterns = allowed.Select(h => new StringSegment(h)).ToList();
        var blocked = new List<string>();
        foreach (var matchHost in matchHosts)
        {
            var host = StripPort(matchHost);
            if (host == "*")
            {
                continue; // matches whatever host filtering lets through
            }

            var admitted = host.StartsWith("*.", StringComparison.Ordinal)
                ? allowed.Any(a => a == "*" || string.Equals(a, host, StringComparison.OrdinalIgnoreCase) ||
                                   (a.StartsWith("*.", StringComparison.Ordinal) &&
                                    host.EndsWith(a[1..], StringComparison.OrdinalIgnoreCase)))
                : HostString.MatchesAny(new StringSegment(host), patterns);
            if (!admitted)
            {
                blocked.Add(matchHost);
            }
        }

        return blocked;
    }

    private static string StripPort(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && !host.EndsWith(']') ? host[..colon] : host;
    }
}
