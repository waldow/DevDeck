using System.Text;
using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Portability;
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
    private readonly CommandTemplateRenderer _renderer;
    private readonly PortProbeService _portProbe;
    private readonly IDevDeckProcessManager _manager;
    private readonly IOptions<DevDeckOptions> _options;

    private readonly PortabilityExporter _exporter;
    private readonly PortabilityImporter _importer;

    public ProxyRoutesController(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        DevDeckProxyConfigProvider provider,
        ProxyDestinationValidator validator,
        CommandTemplateRenderer renderer,
        PortProbeService portProbe,
        IDevDeckProcessManager manager,
        IOptions<DevDeckOptions> options,
        PortabilityExporter exporter,
        PortabilityImporter importer)
    {
        _dbFactory = dbFactory;
        _provider = provider;
        _validator = validator;
        _renderer = renderer;
        _portProbe = portProbe;
        _manager = manager;
        _options = options;
        _exporter = exporter;
        _importer = importer;
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
            DestinationUrlOverride = NormalizeOptional(model.DestinationUrlOverride),
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
        entity.DestinationUrlOverride = NormalizeOptional(model.DestinationUrlOverride);
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

        var destinationUrl = ResolveDestinationUrl(entity.DestinationUrlOverride, entity.DevService);
        var vm = new ProxyRouteTestViewModel
        {
            Id = entity.Id,
            Name = entity.Name,
            Enabled = entity.Enabled,
            RequireHealthyDestination = entity.RequireHealthyDestination,
            MatchPath = entity.MatchPath,
            DestinationUrl = destinationUrl,
            ExampleProxyUrl = $"{_options.Value.ReverseProxy.GatewayBaseUrl.TrimEnd('/')}{Controllers.DashboardController.ExampleProxyPath(entity.MatchPath)}",
        };

        if (entity.DevService is not null)
        {
            vm.HasLinkedService = true;
            vm.UseExternalInstance = entity.DevService.UseExternalInstance;
            vm.ManagedPort = entity.DevService.Port;
            vm.ExternalPort = entity.DevService.ExternalPort;
            vm.EffectivePort = entity.DevService.EffectivePort;
            vm.ServiceRunning = _manager.GetRunningProcess(entity.DevService.Id) is not null;
            var hc = await db.ServiceHealthChecks
                .Where(h => h.DevServiceId == entity.DevService.Id && h.Enabled)
                .OrderByDescending(h => h.LastCheckedUtc)
                .FirstOrDefaultAsync();
            vm.HealthStatus = hc?.LastStatus ?? "Unknown";
        }

        if (destinationUrl is not null && Uri.TryCreate(destinationUrl, UriKind.Absolute, out var uri))
        {
            vm.DestinationPortOpen = await _portProbe.IsEndpointOpenAsync(uri.Host, uri.Port);
        }

        if (ReservedPaths.IsReserved(entity.MatchPath, out var reason, _options.Value.ReverseProxy.AllowCatchAllRoutes))
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

    [HttpGet("Export")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var bundle = await _exporter.ExportRoutesAsync(singleId: null, cancellationToken);
        return JsonDownload(bundle, $"devdeck-routes-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpGet("Export/{id:int}")]
    public async Task<IActionResult> ExportOne(int id, CancellationToken cancellationToken)
    {
        var bundle = await _exporter.ExportRoutesAsync(singleId: id, cancellationToken);
        if (bundle.Routes.Count == 0) return NotFound();
        return JsonDownload(bundle, $"devdeck-route-{Slugify(bundle.Routes[0].Name)}.json");
    }

    [HttpPost("Import")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(4_000_000)]
    public async Task<IActionResult> Import(IFormFile? file, string? json, CancellationToken cancellationToken)
    {
        var payload = await ReadPayloadAsync(file, json, cancellationToken);
        if (payload is null)
        {
            TempData["Error"] = "No JSON provided. Pick a file or paste JSON into the dialog.";
            return RedirectToAction(nameof(Index));
        }

        var result = await _importer.ImportRoutesAsync(payload, cancellationToken);
        if (result.TotalAffected > 0)
        {
            await _provider.ReloadAsync();
        }
        TempData[result.HasErrors ? "Error" : "Info"] = result.ToFlashMessage();
        TempData["ImportWarnings"] = JsonSerializer.Serialize(result.Warnings.Concat(result.Errors).ToList());
        return RedirectToAction(nameof(Index));
    }

    private FileContentResult JsonDownload(object bundle, string fileName)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, bundle.GetType(), PortabilityJson.Options);
        return File(bytes, "application/json", fileName);
    }

    private static async Task<string?> ReadPayloadAsync(IFormFile? file, string? json, CancellationToken cancellationToken)
    {
        if (file is { Length: > 0 })
        {
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    private static string Slugify(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            clean.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }
        return clean.ToString().Trim('-');
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
        if (ReservedPaths.IsReserved(model.MatchPath, out var reason, _options.Value.ReverseProxy.AllowCatchAllRoutes))
        {
            ModelState.AddModelError(nameof(model.MatchPath), reason);
        }

        var destination = NormalizeOptional(model.DestinationUrlOverride);
        if (model.DevServiceId is int sid)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var service = await db.DevServices.FirstOrDefaultAsync(s => s.Id == sid);
            if (service is null)
            {
                ModelState.AddModelError(nameof(model.DevServiceId), "Selected service was not found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(destination))
            {
                destination = service.Url;
            }
            if (string.IsNullOrWhiteSpace(destination))
            {
                ModelState.AddModelError(
                    nameof(model.DestinationUrlOverride),
                    "Selected service does not have a URL. Provide a destination URL override.");
                return;
            }

            var rendered = RenderServiceUrl(destination, service);
            if (rendered.UnknownPlaceholders.Count > 0)
            {
                ModelState.AddModelError(
                    nameof(model.DestinationUrlOverride),
                    $"Destination URL has unknown placeholder(s): {string.Join(", ", rendered.UnknownPlaceholders)}.");
                return;
            }

            destination = rendered.Text;
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

    private string? ResolveDestinationUrl(string? overrideUrl, DevService? service)
    {
        var url = !string.IsNullOrWhiteSpace(overrideUrl) ? overrideUrl : service?.Url;
        if (string.IsNullOrWhiteSpace(url) || service is null)
        {
            return url;
        }

        var rendered = RenderServiceUrl(url, service);
        return rendered.UnknownPlaceholders.Count == 0 ? rendered.Text : url;
    }

    private RenderResult RenderServiceUrl(string url, DevService service)
    {
        return _renderer.Render(
            url,
            CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.EffectivePort, service.WorkingDirectory));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
