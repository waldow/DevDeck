using DevDeck.Web.Data;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/Status")]
public sealed class StatusController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;
    private readonly PortProbeService _portProbe;

    public StatusController(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager manager, PortProbeService portProbe)
    {
        _dbFactory = dbFactory;
        _manager = manager;
        _portProbe = portProbe;
    }

    [HttpGet("Snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken cancellationToken)
    {
        // Polled every DashboardPollingMilliseconds — read-only, so skip change tracking.
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var services = await db.DevServices.AsNoTracking().OrderBy(s => s.DisplayOrder).ToListAsync(cancellationToken);
        var healthChecks = await db.ServiceHealthChecks.AsNoTracking().ToListAsync(cancellationToken);

        // Passthru services have no DevDeck-managed process, so a one-shot TCP probe of the
        // external port is the only way to tell whether the instance is up. Probe in parallel.
        var externalUp = (await Task.WhenAll(
            services.Where(s => s.UseExternalInstance && s.EffectivePort is int)
                    .Select(async s => (id: s.Id,
                                        open: await _portProbe.IsPortOpenAsync(s.EffectivePort!.Value, cancellationToken)))))
            .Where(p => p.open)
            .Select(p => p.id)
            .ToHashSet();

        var payload = services.Select(s =>
        {
            var info = _manager.GetRunningProcess(s.Id);
            var hc = healthChecks
                .Where(h => h.DevServiceId == s.Id && h.Enabled)
                .OrderByDescending(h => h.LastCheckedUtc)
                .FirstOrDefault();

            string runtimeStatus;
            string healthStatus;
            if (s.UseExternalInstance)
            {
                var open = externalUp.Contains(s.Id);
                runtimeStatus = open ? "External" : "Offline";
                healthStatus = open ? (hc?.LastStatus ?? "Unknown") : "NotRunning";
            }
            else
            {
                runtimeStatus = info?.Status.ToString() ?? "Stopped";
                healthStatus = info is null ? "NotRunning" : (hc?.LastStatus ?? "Unknown");
            }

            return new
            {
                id = s.Id,
                name = s.Name,
                serviceType = s.ServiceType,
                runtimeStatus,
                healthStatus,
                port = s.EffectivePort,
                url = s.Url,
                processId = info is null ? (int?)null : SafePid(info),
                runId = info?.ServiceRunId,
            };
        });

        return Json(new { services = payload });
    }

    private static int? SafePid(RunningProcessInfo info)
    {
        try { return info.Process.Id; } catch { return null; }
    }
}
