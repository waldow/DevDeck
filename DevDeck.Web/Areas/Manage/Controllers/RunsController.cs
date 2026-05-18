using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/Runs")]
public sealed class RunsController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;

    public RunsController(IDbContextFactory<DevDeckDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] int? serviceId = null, [FromQuery] string? status = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.ServiceRuns.Include(r => r.DevService).AsQueryable();
        if (serviceId is int id) query = query.Where(r => r.DevServiceId == id);
        if (!string.IsNullOrEmpty(status)) query = query.Where(r => r.Status == status);

        var runs = await query
            .OrderByDescending(r => r.StartedUtc)
            .Take(200)
            .ToListAsync();

        var items = runs.Select(r => new RunsListItem
        {
            Id = r.Id,
            DevServiceId = r.DevServiceId,
            ServiceName = r.DevService?.Name ?? $"#{r.DevServiceId}",
            StartedUtc = r.StartedUtc,
            StoppedUtc = r.StoppedUtc,
            Status = r.Status,
            ExitCode = r.ExitCode,
            LogFilePath = r.LogFilePath,
        }).ToList();

        ViewBag.Services = await db.DevServices.OrderBy(s => s.Name).ToListAsync();
        ViewBag.SelectedServiceId = serviceId;
        ViewBag.SelectedStatus = status;
        return View(items);
    }

    [HttpGet("Details/{id:long}")]
    public async Task<IActionResult> Details(long id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = await db.ServiceRuns.Include(r => r.DevService).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return NotFound();
        return View(new RunDetailsViewModel { Run = run, ServiceName = run.DevService?.Name ?? string.Empty });
    }
}
