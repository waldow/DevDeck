using System.Net;
using DevDeck.Web.Options;

namespace DevDeck.Web.Services.Proxy;

public static class GatewayUrlResolver
{
    public const string DefaultGatewayBaseUrl = "http://localhost:5050";

    public static string ResolveListenUrl(IConfiguration configuration)
    {
        var configured = configuration[$"{DevDeckOptions.SectionName}:ReverseProxy:GatewayBaseUrl"];
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

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return IPAddress.IsLoopback(ip);
        return false;
    }

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
