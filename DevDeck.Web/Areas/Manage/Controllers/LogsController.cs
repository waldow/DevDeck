using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage")]
public sealed class LogsController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;
    private readonly IOptions<DevDeckOptions> _options;

    public LogsController(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager manager, IOptions<DevDeckOptions> options)
    {
        _dbFactory = dbFactory;
        _manager = manager;
        _options = options;
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
    public IActionResult ServiceLogsSnapshot(int id, [FromQuery] long since = 0)
    {
        var info = _manager.GetRunningProcess(id);
        var slice = _manager.GetLiveLogsSince(id, Math.Max(0, since));
        return Json(new
        {
            serviceId = id,
            isRunning = info is not null,
            next = slice.Next,
            dropped = slice.Dropped,
            reset = slice.Reset,
            lines = slice.Lines.Select(l => l.Format()).ToArray(),
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
        if (run is null)
        {
            return NotFound();
        }

        // LogFilePath is system-written, but clamp it to the logs folder anyway so a
        // tampered database row can never read an arbitrary file off disk.
        var fullPath = string.IsNullOrEmpty(run.LogFilePath) ? null : Path.GetFullPath(run.LogFilePath);
        var relative = fullPath is null ? null : Path.GetRelativePath(DevDeckPaths.LogsFolder, fullPath);
        if (fullPath is null || relative is null ||
            relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
            !System.IO.File.Exists(fullPath))
        {
            // Deleted by log retention (or by hand): say so on the run's page rather than a bare 404.
            TempData["Error"] = $"The log file of run #{run.Id} no longer exists; run logs are deleted after " +
                                $"{_options.Value.LogRetentionDays} day(s) (DevDeck:LogRetentionDays).";
            return RedirectToAction("Details", "Runs", new { id = run.Id });
        }

        // Open with FileShare.ReadWrite so a still-running service (whose LogFileWriter holds
        // an open FileAccess.Write handle) doesn't cause a sharing violation. The framework
        // disposes the returned stream after streaming it to the response.
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        return File(stream, "text/plain", Path.GetFileName(fullPath));
    }
}
