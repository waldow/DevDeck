using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/Profiles")]
public sealed class ProfilesController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;

    public ProfilesController(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager manager)
    {
        _dbFactory = dbFactory;
        _manager = manager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profiles = await db.LaunchProfiles
            .Include(p => p.Services)
            .OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
            .ToListAsync();
        return View(profiles);
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create()
    {
        var vm = new ProfileEditViewModel();
        await PopulateAvailableServicesAsync(vm);
        return View("Edit", vm);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProfileEditViewModel model)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.LaunchProfiles.AnyAsync(p => p.Name == model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "A profile with this name already exists.");
        }
        if (!ModelState.IsValid)
        {
            await PopulateAvailableServicesAsync(model);
            return View("Edit", model);
        }

        var profile = new LaunchProfile
        {
            Name = model.Name,
            Description = model.Description,
            IsDefault = model.IsDefault,
            DisplayOrder = model.DisplayOrder,
        };
        foreach (var row in model.Services.Where(r => r.Include))
        {
            profile.Services.Add(new LaunchProfileService
            {
                DevServiceId = row.DevServiceId,
                StartOrder = row.StartOrder,
                StartDelaySeconds = row.StartDelaySeconds,
            });
        }
        db.LaunchProfiles.Add(profile);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.LaunchProfiles
            .Include(p => p.Services).ThenInclude(s => s.DevService)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();

        var services = await db.DevServices.OrderBy(s => s.Name).ToListAsync();

        var vm = new ProfileEditViewModel
        {
            Id = profile.Id,
            Name = profile.Name,
            Description = profile.Description,
            IsDefault = profile.IsDefault,
            DisplayOrder = profile.DisplayOrder,
            Services = services.Select(s =>
            {
                var existing = profile.Services.FirstOrDefault(ps => ps.DevServiceId == s.Id);
                return new ProfileServiceRow
                {
                    DevServiceId = s.Id,
                    ServiceName = s.Name,
                    Include = existing is not null,
                    StartOrder = existing?.StartOrder ?? 0,
                    StartDelaySeconds = existing?.StartDelaySeconds ?? 0,
                };
            }).ToList(),
        };
        return View(vm);
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ProfileEditViewModel model)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.LaunchProfiles
            .Include(p => p.Services)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();

        if (await db.LaunchProfiles.AnyAsync(p => p.Name == model.Name && p.Id != id))
        {
            ModelState.AddModelError(nameof(model.Name), "A profile with this name already exists.");
        }
        if (!ModelState.IsValid)
        {
            await PopulateAvailableServicesAsync(model);
            return View(model);
        }

        profile.Name = model.Name;
        profile.Description = model.Description;
        profile.IsDefault = model.IsDefault;
        profile.DisplayOrder = model.DisplayOrder;

        db.LaunchProfileServices.RemoveRange(profile.Services);
        profile.Services.Clear();
        foreach (var row in model.Services.Where(r => r.Include))
        {
            profile.Services.Add(new LaunchProfileService
            {
                DevServiceId = row.DevServiceId,
                StartOrder = row.StartOrder,
                StartDelaySeconds = row.StartDelaySeconds,
            });
        }

        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.LaunchProfiles.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();
        db.LaunchProfiles.Remove(profile);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int id, CancellationToken cancellationToken)
    {
        var result = await _manager.StartProfileAsync(id, cancellationToken);
        TempData[result.Success ? "Info" : "Error"] = result.Success
            ? $"Started {result.Outcomes.Count(o => o.Success)}/{result.Outcomes.Count} services."
            : $"Some services failed: {string.Join("; ", result.Outcomes.Where(o => !o.Success).Select(o => $"{o.ServiceName}: {o.Message}"))}";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost("{id:int}/Stop")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(int id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var serviceIds = await db.LaunchProfileServices
            .Where(s => s.LaunchProfileId == id)
            .Select(s => s.DevServiceId)
            .ToListAsync(cancellationToken);
        foreach (var sid in serviceIds)
        {
            await _manager.StopServiceAsync(sid, cancellationToken);
        }
        TempData["Info"] = $"Stopped {serviceIds.Count} services.";
        return RedirectToAction("Index", "Dashboard");
    }

    private async Task PopulateAvailableServicesAsync(ProfileEditViewModel vm)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var services = await db.DevServices.OrderBy(s => s.Name).ToListAsync();
        vm.AvailableServices = services.Select(s => new ServiceOption(s.Id, s.Name, s.Url)).ToList();
        if (vm.Services.Count == 0)
        {
            vm.Services = services.Select(s => new ProfileServiceRow
            {
                DevServiceId = s.Id,
                ServiceName = s.Name,
                Include = false,
            }).ToList();
        }
    }
}
