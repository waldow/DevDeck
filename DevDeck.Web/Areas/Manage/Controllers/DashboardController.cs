using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Options;
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

    public DashboardController(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager manager,
        IOptions<DevDeckOptions> options)
    {
        _dbFactory = dbFactory;
        _manager = manager;
        _options = options;
    }

    public async Task<IActionResult> Index()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var services = await db.DevServices.AsNoTracking().OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync();
        var healthChecks = await db.ServiceHealthChecks.AsNoTracking().ToListAsync();
        var proxies = await db.ProxyRoutes.AsNoTracking().Where(r => r.Enabled && r.ShowOnDashboard).ToListAsync();

        var gateway = _options.Value.ReverseProxy.GatewayBaseUrl.TrimEnd('/');

        var cards = services.Select(s =>
        {
            var info = _manager.GetRunningProcess(s.Id);
            var hc = healthChecks
                .Where(h => h.DevServiceId == s.Id && h.Enabled)
                .OrderByDescending(h => h.LastCheckedUtc)
                .FirstOrDefault();

            string? proxyUrl = null;
            var route = proxies.FirstOrDefault(p => p.DevServiceId == s.Id);
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
                RuntimeStatus = s.UseExternalInstance ? "External" : (info?.Status.ToString() ?? "Stopped"),
                HealthStatus = s.UseExternalInstance
                    ? (hc?.LastStatus ?? "Unknown")
                    : (info is null ? "NotRunning" : (hc?.LastStatus ?? "Unknown")),
                Port = s.EffectivePort,
                Url = s.Url,
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
