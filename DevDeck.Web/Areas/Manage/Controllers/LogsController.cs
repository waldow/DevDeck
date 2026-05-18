using DevDeck.Web.Data;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage")]
public sealed class LogsController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;

    public LogsController(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager manager)
    {
        _dbFactory = dbFactory;
        _manager = manager;
    }

    [HttpGet("Services/{id:int}/Logs")]
    public async Task<IActionResult> ServiceLogs(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var service = await db.DevServices.FirstOrDefaultAsync(s => s.Id == id);
        if (service is null) return NotFound();

        ViewBag.ServiceName = service.Name;
        ViewBag.ServiceId = id;
        ViewBag.IsRunning = _manager.GetRunningProcess(id) is not null;
        return View("Logs");
    }

    [HttpGet("Services/{id:int}/LogsSnapshot")]
    public IActionResult ServiceLogsSnapshot(int id, [FromQuery] int sinceCount = 0)
    {
        var info = _manager.GetRunningProcess(id);
        var lines = _manager.GetLiveLogs(id);
        var newSlice = sinceCount > 0 && lines.Count > sinceCount
            ? lines.Skip(sinceCount).Select(l => l.Format()).ToArray()
            : lines.Select(l => l.Format()).ToArray();
        return Json(new
        {
            serviceId = id,
            isRunning = info is not null,
            totalCount = lines.Count,
            lines = newSlice,
        });
    }

    [HttpPost("Services/{id:int}/LogsClear")]
    [ValidateAntiForgeryToken]
    public IActionResult ServiceLogsClear(int id)
    {
        _manager.ClearLiveLogs(id);
        return RedirectToAction(nameof(ServiceLogs), new { id });
    }

    [HttpGet("Runs/{runId:long}/Download")]
    public async Task<IActionResult> Download(long runId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = await db.ServiceRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run is null || string.IsNullOrEmpty(run.LogFilePath) || !System.IO.File.Exists(run.LogFilePath))
        {
            return NotFound();
        }
        var bytes = await System.IO.File.ReadAllBytesAsync(run.LogFilePath);
        return File(bytes, "text/plain", Path.GetFileName(run.LogFilePath));
    }
}
