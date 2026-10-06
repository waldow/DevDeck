using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
public sealed class DashboardController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;
    private readonly IOptions<DevDeckOptions> _options;
    private readonly HealthStatusCache _healthStatusCache;
    private readonly CommandTemplateRenderer _renderer;

    public DashboardController(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager manager,
        IOptions<DevDeckOptions> options,
        HealthStatusCache healthStatusCache,
        CommandTemplateRenderer renderer)
    {
        _dbFactory = dbFactory;
        _manager = manager;
        _options = options;
        _healthStatusCache = healthStatusCache;
        _renderer = renderer;
    }

    public async Task<IActionResult> Index()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var services = await db.DevServices.AsNoTracking().OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync();
        var proxies = await db.ProxyRoutes.AsNoTracking().Where(r => r.Enabled && r.ShowOnDashboard).ToListAsync();

        var gateway = _options.Value.ReverseProxy.GatewayBaseUrl.TrimEnd('/');

        var cards = services.Select(s =>
        {
            var info = _manager.GetRunningProcess(s.Id);

            // The link is on the gateway's own host, so only a route without Match hosts is sure
            // to serve it; the lowest Order wins, as it does in routing.
            string? proxyUrl = null;
            var route = _options.Value.ReverseProxy.Enabled
                ? proxies.Where(p => p.DevServiceId == s.Id && string.IsNullOrWhiteSpace(p.MatchHostsCsv))
                         .OrderBy(p => p.Order).ThenBy(p => p.Id)
                         .FirstOrDefault()
                : null;
            if (route is not null)
            {
                proxyUrl = $"{gateway}{ExampleProxyPath(route.MatchPath)}";
            }

            return new DashboardCard
            {
                Id = s.Id,
                Name = s.Name,
                ServiceType = s.ServiceType,
                Enabled = s.Enabled,
                UseExternalInstance = s.UseExternalInstance,
                // Passthru runtime is determined by the polled snapshot's port probe; render an
                // optimistic "External" initially and let the first tick correct it to "Offline".
                RuntimeStatus = s.UseExternalInstance ? "External"
                    : info?.Status.ToString() ?? (_manager.IsServiceStarting(s.Id) ? "Starting" : "Stopped"),
                HealthStatus = _healthStatusCache.GetDisplayStatus(s.Id, s.UseExternalInstance || info is not null),
                Port = s.EffectivePort,
                Url = DirectUrl(s),
                ProxyUrl = proxyUrl,
                ProcessId = info is null ? null : SafePid(info),
                RunId = info?.ServiceRunId,
            };
        }).ToList();

        return View(new DashboardViewModel
        {
            Cards = cards,
            PollingMilliseconds = _options.Value.DashboardPollingMilliseconds,
        });
    }

    // The service URL with {port} etc. filled in, as the proxy and health checks use it — the
    // stored value is usually the preset's "http://localhost:{port}" template. Null when a
    // placeholder can't be rendered (e.g. no port), rather than an unusable link.
    private string? DirectUrl(DevService service)
    {
        if (string.IsNullOrWhiteSpace(service.Url))
        {
            return null;
        }

        var rendered = _renderer.Render(
            service.Url,
            CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.EffectivePort, service.WorkingDirectory));
        // Only http(s): an imported "javascript:" URL must not become a clickable link.
        return rendered.UnknownPlaceholders.Count == 0 &&
               Uri.TryCreate(rendered.Text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? rendered.Text
            : null;
    }

    private static int? SafePid(RunningProcessInfo info)
    {
        try { return info.Process.Id; } catch { return null; }
    }

    public static string ExampleProxyPath(string matchPath)
    {
        var p = matchPath;
        var i = p.IndexOf('{');
        if (i >= 0) p = p.Substring(0, i);
        if (!p.StartsWith('/')) p = "/" + p;
        return p.TrimEnd('/');
    }
}
