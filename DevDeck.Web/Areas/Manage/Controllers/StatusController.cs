using DevDeck.Web.Data;
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

    public StatusController(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager manager)
    {
        _dbFactory = dbFactory;
        _manager = manager;
    }

    [HttpGet("Snapshot")]
    public async Task<IActionResult> Snapshot()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var services = await db.DevServices.OrderBy(s => s.DisplayOrder).ToListAsync();
        var healthChecks = await db.ServiceHealthChecks.ToListAsync();

        var payload = services.Select(s =>
        {
            var info = _manager.GetRunningProcess(s.Id);
            var hc = healthChecks
                .Where(h => h.DevServiceId == s.Id && h.Enabled)
                .OrderByDescending(h => h.LastCheckedUtc)
                .FirstOrDefault();
            return new
            {
                id = s.Id,
                name = s.Name,
                serviceType = s.ServiceType,
                runtimeStatus = info?.Status.ToString() ?? "Stopped",
                healthStatus = info is null ? "NotRunning" : (hc?.LastStatus ?? "Unknown"),
                port = s.Port,
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
