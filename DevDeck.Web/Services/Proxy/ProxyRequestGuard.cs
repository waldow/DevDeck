using DevDeck.Web.Options;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Runtime;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Proxy;

public sealed class ProxyRequestGuard
{
    private readonly IDevDeckProcessManager _processManager;
    private readonly HealthStatusCache _healthStatusCache;
    private readonly IOptionsMonitor<DevDeckOptions> _options;

    public ProxyRequestGuard(
        IDevDeckProcessManager processManager,
        HealthStatusCache healthStatusCache,
        IOptionsMonitor<DevDeckOptions> options)
    {
        _processManager = processManager;
        _healthStatusCache = healthStatusCache;
        _options = options;
    }

    public async Task<bool> AllowRequestAsync(HttpContext context)
    {
        var metadata = context.GetRouteModel()?.Config.Metadata;
        if (metadata is null || !TryGetInt(metadata, "DevDeck.ServiceId", out var serviceId))
        {
            return true;
        }

        var isRunning = _processManager.GetRunningProcess(serviceId) is not null;
        if (!isRunning)
        {
            var routeAllowsAutoStart = TryGetBool(metadata, "DevDeck.AutoStartService");
            var globalAutoStartEnabled = _options.CurrentValue.ReverseProxy.EnableAutoStartOnRequest;
            if (!routeAllowsAutoStart || !globalAutoStartEnabled)
            {
                await WriteUnavailableAsync(context, serviceId, "The proxied service is not running.");
                return false;
            }

            var start = await _processManager.StartServiceAsync(serviceId, context.RequestAborted);
            if (!start.Success)
            {
                await WriteUnavailableAsync(context, serviceId, start.Error ?? "The proxied service failed to start.");
                return false;
            }

            var ready = await WaitForReadyAsync(serviceId, context.RequestAborted);
            if (!ready)
            {
                await WriteUnavailableAsync(context, serviceId, "The proxied service did not become ready before the timeout.");
                return false;
            }
        }

        if (TryGetBool(metadata, "DevDeck.RequireHealthyDestination") && !_healthStatusCache.IsHealthy(serviceId))
        {
            await WriteUnavailableAsync(context, serviceId, $"The proxied service is not healthy ({_healthStatusCache.Get(serviceId)}).");
            return false;
        }

        return true;
    }

    private async Task<bool> WaitForReadyAsync(int serviceId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var running = _processManager.GetRunningProcess(serviceId);
            if (running is null)
            {
                return false;
            }

            if (running.Port is null || await PortProbeService.IsPortOpenAsync(running.Port.Value, cancellationToken))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> metadata, string key, out int value)
    {
        value = 0;
        return metadata.TryGetValue(key, out var text) && int.TryParse(text, out value);
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var text) && bool.TryParse(text, out var value) && value;

    private static async Task WriteUnavailableAsync(HttpContext context, int serviceId, string message)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Service unavailable</title></head>
            <body>
                <h1>Service unavailable</h1>
                <p>{System.Net.WebUtility.HtmlEncode(message)}</p>
                <p><a href="/Manage/Services/Details/{serviceId}">Open service in DevDeck</a></p>
            </body>
            </html>
            """);
    }
}
