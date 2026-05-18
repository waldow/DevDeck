using System.Net;
using DevDeck.Web.Options;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Proxy;

public sealed class ProxyDestinationValidator
{
    private readonly IOptionsMonitor<DevDeckOptions> _options;

    public ProxyDestinationValidator(IOptionsMonitor<DevDeckOptions> options)
    {
        _options = options;
    }

    public ProxyDestinationValidator(bool allowExternal)
    {
        _options = new StaticOptions(allowExternal);
    }

    public ValidationResult Validate(string destinationUrl)
    {
        if (string.IsNullOrWhiteSpace(destinationUrl))
        {
            return ValidationResult.Fail("Destination URL is required.");
        }

        if (!Uri.TryCreate(destinationUrl, UriKind.Absolute, out var uri))
        {
            return ValidationResult.Fail("Destination URL must be an absolute http/https URL.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidationResult.Fail("Destination URL must use http or https.");
        }

        var host = uri.Host;
        var isLocal = IsLocalHost(host);
        var isPrivate = IsPrivateIp(host);

        if (isLocal || isPrivate)
        {
            return ValidationResult.Ok();
        }

        if (_options.CurrentValue.ReverseProxy.AllowExternalDestinations)
        {
            return ValidationResult.Ok();
        }

        return ValidationResult.Fail(
            $"Destination host '{host}' is outside localhost/private networks. " +
            "Enable DevDeck:ReverseProxy:AllowExternalDestinations to permit external destinations.");
    }

    public static bool IsLocalHost(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("127.0.0.1", StringComparison.Ordinal)) return true;
        if (host.Equals("::1", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static bool IsPrivateIp(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        return false;
    }

    public sealed record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Ok() => new(true, null);
        public static ValidationResult Fail(string error) => new(false, error);
    }

    private sealed class StaticOptions : IOptionsMonitor<DevDeckOptions>
    {
        private readonly DevDeckOptions _value;
        public StaticOptions(bool allowExternal)
        {
            _value = new DevDeckOptions();
            _value.ReverseProxy.AllowExternalDestinations = allowExternal;
        }
        public DevDeckOptions CurrentValue => _value;
        public DevDeckOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<DevDeckOptions, string?> listener) => null;
    }
}
