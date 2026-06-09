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
        var newSlice = sinceCount <= 0
            ? lines.Select(l => l.Format()).ToArray()
            : lines.Skip(Math.Min(sinceCount, lines.Count)).Select(l => l.Format()).ToArray();
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
        if (run is null || string.IsNullOrEmpty(run.LogFilePath))
        {
            return NotFound();
        }

        // LogFilePath is system-written, but clamp it to the logs folder anyway so a
        // tampered database row can never read an arbitrary file off disk.
        var fullPath = Path.GetFullPath(run.LogFilePath);
        var relative = Path.GetRelativePath(DevDeckPaths.LogsFolder, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
            !System.IO.File.Exists(fullPath))
        {
            return NotFound();
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
