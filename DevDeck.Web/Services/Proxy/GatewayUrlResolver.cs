using DevDeck.Web.Options;

namespace DevDeck.Web.Services.Proxy;

public static class GatewayUrlResolver
{
    public const string DefaultGatewayBaseUrl = "http://localhost:5050";

    public static string ResolveListenUrl(IConfiguration configuration) =>
        Normalize(configuration[$"{DevDeckOptions.SectionName}:ReverseProxy:GatewayBaseUrl"]);

    /// <summary>The gateway origin for a configured GatewayBaseUrl, or the default when it is unusable.</summary>
    public static string Normalize(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultGatewayBaseUrl;
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri) ||
            !IsHttp(uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return DefaultGatewayBaseUrl;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>True for a bind-to-every-interface host such as 0.0.0.0 or [::].</summary>
    public static bool IsAnyAddress(string host) =>
        System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) &&
        (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any));

    /// <summary>
    /// True when the listen URL binds only to this machine. A non-loopback bind (0.0.0.0,
    /// a LAN IP, a DNS name) exposes the unauthenticated Manage UI and proxy to the network,
    /// so callers warn on it.
    /// </summary>
    public static bool IsLoopbackHost(string listenUrl)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        return ProxyDestinationValidator.IsLocalHost(uri.Host);
    }

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
