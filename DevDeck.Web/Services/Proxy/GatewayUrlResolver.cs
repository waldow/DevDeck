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

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
