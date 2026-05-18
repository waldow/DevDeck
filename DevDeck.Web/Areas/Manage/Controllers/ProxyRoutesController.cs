using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/ProxyRoutes")]
public sealed class ProxyRoutesController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly DevDeckProxyConfigProvider _provider;
    private readonly ProxyDestinationValidator _validator;
    private readonly PortProbeService _portProbe;
    private readonly IDevDeckProcessManager _manager;
    private readonly IOptions<DevDeckOptions> _options;

    public ProxyRoutesController(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        DevDeckProxyConfigProvider provider,
        ProxyDestinationValidator validator,
        PortProbeService portProbe,
        IDevDeckProcessManager manager,
        IOptions<DevDeckOptions> options)
    {
        _dbFactory = dbFactory;
        _provider = provider;
        _validator = validator;
        _portProbe = portProbe;
        _manager = manager;
        _options = options;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var routes = await db.ProxyRoutes
            .Include(r => r.DevService)
            .OrderBy(r => r.Order).ThenBy(r => r.Name)
            .ToListAsync();
        ViewBag.Warnings = _provider.LastWarnings;
        ViewBag.GatewayBaseUrl = _options.Value.ReverseProxy.GatewayBaseUrl;
        return View(routes);
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create()
    {
        var vm = new ProxyRouteEditViewModel();
        await PopulateAvailableServicesAsync(vm);
        return View("Edit", vm);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ProxyRouteEditViewModel model)
    {
        await ValidateAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateAvailableServicesAsync(model);
            return View("Edit", model);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = new ProxyRoute
        {
            Name = model.Name,
            Enabled = model.Enabled,
            DevServiceId = model.DevServiceId,
            DestinationUrlOverride = model.DestinationUrlOverride,
            MatchPath = model.MatchPath,
            MatchHostsCsv = model.MatchHostsCsv,
            Order = model.Order,
            PathTransformMode = model.PathTransformMode,
            PathPrefixToRemove = model.PathPrefixToRemove,
            PathPrefixToAdd = model.PathPrefixToAdd,
            PathSet = model.PathSet,
            PreserveHostHeader = model.PreserveHostHeader,
            AutoStartService = model.AutoStartService,
            RequireHealthyDestination = model.RequireHealthyDestination,
            TimeoutSeconds = model.TimeoutSeconds,
            AuthorizationPolicy = model.AuthorizationPolicy,
            ShowOnDashboard = model.ShowOnDashboard,
        };
        db.ProxyRoutes.Add(entity);
        await db.SaveChangesAsync();
        await _provider.ReloadAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.ProxyRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return NotFound();

        var vm = new ProxyRouteEditViewModel
        {
            Id = entity.Id,
            Name = entity.Name,
            Enabled = entity.Enabled,
            DevServiceId = entity.DevServiceId,
            DestinationUrlOverride = entity.DestinationUrlOverride,
            MatchPath = entity.MatchPath,
            MatchHostsCsv = entity.MatchHostsCsv,
            Order = entity.Order,
            PathTransformMode = entity.PathTransformMode,
            PathPrefixToRemove = entity.PathPrefixToRemove,
            PathPrefixToAdd = entity.PathPrefixToAdd,
            PathSet = entity.PathSet,
            PreserveHostHeader = entity.PreserveHostHeader,
            AutoStartService = entity.AutoStartService,
            RequireHealthyDestination = entity.RequireHealthyDestination,
            TimeoutSeconds = entity.TimeoutSeconds,
            AuthorizationPolicy = entity.AuthorizationPolicy,
            ShowOnDashboard = entity.ShowOnDashboard,
        };
        await PopulateAvailableServicesAsync(vm);
        return View(vm);
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ProxyRouteEditViewModel model)
    {
        await ValidateAsync(model);
        if (!ModelState.IsValid)
        {
            await PopulateAvailableServicesAsync(model);
            return View(model);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.ProxyRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return NotFound();

        entity.Name = model.Name;
        entity.Enabled = model.Enabled;
        entity.DevServiceId = model.DevServiceId;
        entity.DestinationUrlOverride = model.DestinationUrlOverride;
        entity.MatchPath = model.MatchPath;
        entity.MatchHostsCsv = model.MatchHostsCsv;
        entity.Order = model.Order;
        entity.PathTransformMode = model.PathTransformMode;
        entity.PathPrefixToRemove = model.PathPrefixToRemove;
        entity.PathPrefixToAdd = model.PathPrefixToAdd;
        entity.PathSet = model.PathSet;
        entity.PreserveHostHeader = model.PreserveHostHeader;
        entity.AutoStartService = model.AutoStartService;
        entity.RequireHealthyDestination = model.RequireHealthyDestination;
        entity.TimeoutSeconds = model.TimeoutSeconds;
        entity.AuthorizationPolicy = model.AuthorizationPolicy;
        entity.ShowOnDashboard = model.ShowOnDashboard;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        await _provider.ReloadAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.ProxyRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return NotFound();
        db.ProxyRoutes.Remove(entity);
        await db.SaveChangesAsync();
        await _provider.ReloadAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/Enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enable(int id) => await SetEnabledAsync(id, true);

    [HttpPost("{id:int}/Disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(int id) => await SetEnabledAsync(id, false);

    [HttpPost("Reload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reload()
    {
        await _provider.ReloadAsync();
        TempData["Info"] = "Proxy config reloaded.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:int}/Test")]
    public async Task<IActionResult> Test(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.ProxyRoutes
            .Include(r => r.DevService)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return NotFound();

        var destinationUrl = entity.DestinationUrlOverride ?? entity.DevService?.Url;
        var vm = new ProxyRouteTestViewModel
        {
            Id = entity.Id,
            Name = entity.Name,
            Enabled = entity.Enabled,
            MatchPath = entity.MatchPath,
            DestinationUrl = destinationUrl,
            ExampleProxyUrl = $"{_options.Value.ReverseProxy.GatewayBaseUrl.TrimEnd('/')}{Controllers.DashboardController.ExampleProxyPath(entity.MatchPath)}",
        };

        if (entity.DevService is not null)
        {
            vm.ServiceRunning = _manager.GetRunningProcess(entity.DevService.Id) is not null;
            if (entity.DevService.Port is int port)
            {
                vm.DestinationPortOpen = await _portProbe.IsPortOpenAsync(port);
            }
            var hc = await db.ServiceHealthChecks
                .Where(h => h.DevServiceId == entity.DevService.Id && h.Enabled)
                .OrderByDescending(h => h.LastCheckedUtc)
                .FirstOrDefaultAsync();
            vm.HealthStatus = hc?.LastStatus ?? "Unknown";
        }
        else if (destinationUrl is not null && Uri.TryCreate(destinationUrl, UriKind.Absolute, out var uri))
        {
            vm.DestinationPortOpen = await _portProbe.IsPortOpenAsync(uri.Port);
        }

        if (ReservedPaths.IsReserved(entity.MatchPath, out var reason))
        {
            vm.Warnings.Add(reason);
        }
        var validation = _validator.Validate(destinationUrl ?? string.Empty);
        if (!validation.IsValid && validation.Error is not null)
        {
            vm.Warnings.Add(validation.Error);
        }

        return View(vm);
    }

    private async Task<IActionResult> SetEnabledAsync(int id, bool enabled)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.ProxyRoutes.FirstOrDefaultAsync(r => r.Id == id);
        if (entity is null) return NotFound();
        entity.Enabled = enabled;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await _provider.ReloadAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(ProxyRouteEditViewModel model)
    {
        if (ReservedPaths.IsReserved(model.MatchPath, out var reason))
        {
            ModelState.AddModelError(nameof(model.MatchPath), reason);
        }

        var destination = model.DestinationUrlOverride;
        if (string.IsNullOrEmpty(destination) && model.DevServiceId is int sid)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            destination = (await db.DevServices.FirstOrDefaultAsync(s => s.Id == sid))?.Url;
        }
        if (!string.IsNullOrEmpty(destination))
        {
            var v = _validator.Validate(destination);
            if (!v.IsValid && v.Error is not null)
            {
                ModelState.AddModelError(nameof(model.DestinationUrlOverride), v.Error);
            }
        }
        else if (model.DevServiceId is null)
        {
            ModelState.AddModelError(nameof(model.DevServiceId), "Either link a service or provide a destination URL override.");
        }
    }

    private async Task PopulateAvailableServicesAsync(ProxyRouteEditViewModel vm)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        vm.AvailableServices = (await db.DevServices.OrderBy(s => s.Name).ToListAsync())
            .Select(s => new ServiceOption(s.Id, s.Name, s.Url))
            .ToList();
    }
}
