using System.Diagnostics;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Runtime;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace DevDeck.Web.Services.Proxy;

/// <summary>
/// Writes a pair of <c>PRX</c> log lines (inbound request, outbound response) into the owning
/// service's log stream for each request the reverse proxy forwards to it. Enabled by
/// <see cref="DevDeckReverseProxyOptions.LogProxyRequests"/>.
/// </summary>
public sealed class ProxyRequestLogger
{
    private readonly IDevDeckProcessManager _processManager;
    private readonly IOptionsMonitor<DevDeckOptions> _options;

    public ProxyRequestLogger(
        IDevDeckProcessManager processManager,
        IOptionsMonitor<DevDeckOptions> options)
    {
        _processManager = processManager;
        _options = options;
    }

    public async Task LogExchangeAsync(HttpContext context, Func<Task> next)
    {
        if (!_options.CurrentValue.ReverseProxy.LogProxyRequests)
        {
            await next();
            return;
        }

        var metadata = context.GetRouteModel()?.Config.Metadata;
        if (metadata is null || !TryGetInt(metadata, "DevDeck.ServiceId", out var serviceId))
        {
            await next();
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "-";
        var verb = context.Request.Method;
        var path = context.Request.Path.ToString();
        var pathAndQuery = path + context.Request.QueryString;

        _processManager.AppendProxyLog(serviceId, FormatInbound(ip, verb, pathAndQuery));

        var sw = Stopwatch.StartNew();
        try
        {
            await next();
        }
        finally
        {
            sw.Stop();
            var dest = context.Features.Get<IReverseProxyFeature>()?.ProxiedDestination?.Model.Config.Address ?? "-";
            var status = context.Response.StatusCode;
            var size = context.Response.ContentLength;
            _processManager.AppendProxyLog(serviceId,
                FormatOutbound(ip, verb, path, dest, status, sw.ElapsedMilliseconds, size));
        }
    }

    public static string FormatInbound(string ip, string verb, string pathAndQuery) =>
        $"{ip} --> {verb} {pathAndQuery}";

    public static string FormatOutbound(string ip, string verb, string path, string dest, int status, long elapsedMs, long? size) =>
        $"{ip} <-- {status} {verb} {path} -> {dest} {elapsedMs}ms {FormatSize(size)}";

    public static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return "-";
        }

        double value = bytes.Value;
        if (value < 1024)
        {
            return $"{bytes.Value} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return $"{value:0.#} {units[unit]}";
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> metadata, string key, out int value)
    {
        value = 0;
        return metadata.TryGetValue(key, out var text) && int.TryParse(text, out value);
    }
}
