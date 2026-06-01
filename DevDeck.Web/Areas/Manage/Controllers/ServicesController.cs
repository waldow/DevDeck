using System.Text;
using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Portability;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Areas.Manage.Controllers;

[Area("Manage")]
[Route("Manage/Services")]
public sealed class ServicesController : Controller
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _manager;
    private readonly CommandPresetProvider _presets;
    private readonly PortProbeService _portProbe;
    private readonly PortabilityExporter _exporter;
    private readonly PortabilityImporter _importer;
    private readonly DevDeckProxyConfigProvider _proxyProvider;
    private readonly IOptions<DevDeckOptions> _options;

    public ServicesController(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager manager,
        CommandPresetProvider presets,
        PortProbeService portProbe,
        PortabilityExporter exporter,
        PortabilityImporter importer,
        DevDeckProxyConfigProvider proxyProvider,
        IOptions<DevDeckOptions> options)
    {
        _dbFactory = dbFactory;
        _manager = manager;
        _presets = presets;
        _portProbe = portProbe;
        _exporter = exporter;
        _importer = importer;
        _proxyProvider = proxyProvider;
        _options = options;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var services = await db.DevServices
            .Include(s => s.HealthChecks)
            .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
            .ToListAsync();
        ViewBag.Presets = _presets.All();
        ViewBag.RunningIds = _manager.GetRunningProcesses().Select(r => r.DevServiceId).ToHashSet();
        ViewBag.PollMs = _options.Value.DashboardPollingMilliseconds;

        // Best-effort, one-shot port-conflict sweep (this GET is not polled).
        // Probe every configured port in parallel; flag the ones held by a
        // non-DevDeck process so the row can surface a ⚠ marker.
        // External (passthru) services are expected to have their port held by the instance we
        // forward to, so they are not conflicts — skip them in the sweep.
        var probes = await Task.WhenAll(
            services.Where(s => s.Port is int && !s.UseExternalInstance)
                    .Select(async s => (port: s.Port!.Value,
                                        status: await _portProbe.ProbeAsync(s.Port!.Value, s.Id))));
        ViewBag.ConflictPorts = probes
            .Where(p => p.status == PortStatus.UsedByOtherProcess)
            .Select(p => p.port)
            .ToHashSet();

        return View(services);
    }

    [HttpGet("Create")]
    public IActionResult Create([FromQuery] string? preset)
    {
        var vm = new ServiceEditViewModel();
        if (preset is not null)
        {
            var p = _presets.Find(preset);
            if (p is not null)
            {
                vm.ServiceType = p.Key;
                vm.StartCommand = p.StartCommand;
                vm.StartArguments = p.StartArguments;
                vm.Port = p.DefaultPort;
                vm.Url = p.UrlTemplate;
                if (p.HealthCheckUrlTemplate is not null)
                {
                    vm.HealthChecks.Add(new HealthCheckEditRow { Url = p.HealthCheckUrlTemplate, ExpectedStatusCode = 200 });
                }
                if (p.DefaultEnvironment is not null)
                {
                    foreach (var env in p.DefaultEnvironment)
                    {
                        vm.EnvironmentVariables.Add(new EnvVarEditRow { Key = env.Key, Value = env.Value });
                    }
                }
            }
        }
        ViewBag.Presets = _presets.All();
        return View("Edit", vm);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceEditViewModel model)
    {
        ViewBag.Presets = _presets.All();
        if (!ModelState.IsValid)
        {
            return View("Edit", model);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        if (await db.DevServices.AnyAsync(s => s.Name == model.Name))
        {
            ModelState.AddModelError(nameof(model.Name), "A service with this name already exists.");
            return View("Edit", model);
        }

        var entity = new DevService
        {
            Name = model.Name,
            ServiceType = model.ServiceType,
            WorkingDirectory = model.WorkingDirectory,
            StartCommand = model.StartCommand,
            StartArguments = model.StartArguments,
            StopCommand = model.StopCommand,
            StopArguments = model.StopArguments,
            Url = model.Url,
            Port = model.Port,
            Enabled = model.Enabled,
            AutoStart = model.AutoStart,
            UseExternalInstance = model.UseExternalInstance,
            ExternalPort = model.ExternalPort,
            DisplayOrder = model.DisplayOrder,
        };
        ApplyChildren(entity, model);
        db.DevServices.Add(entity);
        await db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.DevServices
            .Include(s => s.EnvironmentVariables)
            .Include(s => s.HealthChecks)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null) return NotFound();

        ViewBag.Presets = _presets.All();
        ViewBag.IsRunning = _manager.GetRunningProcess(id) is not null;

        var vm = new ServiceEditViewModel
        {
            Id = entity.Id,
            Name = entity.Name,
            ServiceType = entity.ServiceType,
            WorkingDirectory = entity.WorkingDirectory,
            StartCommand = entity.StartCommand,
            StartArguments = entity.StartArguments,
            StopCommand = entity.StopCommand,
            StopArguments = entity.StopArguments,
            Url = entity.Url,
            Port = entity.Port,
            Enabled = entity.Enabled,
            AutoStart = entity.AutoStart,
            UseExternalInstance = entity.UseExternalInstance,
            ExternalPort = entity.ExternalPort,
            DisplayOrder = entity.DisplayOrder,
            EnvironmentVariables = entity.EnvironmentVariables
                .Select(e => new EnvVarEditRow
                {
                    Id = e.Id,
                    Key = e.Key,
                    Value = e.IsSecret ? EnvVarEditRow.SecretPlaceholder : e.Value,
                    IsSecret = e.IsSecret,
                })
                .ToList(),
            HealthChecks = entity.HealthChecks
                .Select(h => new HealthCheckEditRow
                {
                    Id = h.Id, Url = h.Url, ExpectedStatusCode = h.ExpectedStatusCode,
                    IntervalSeconds = h.IntervalSeconds, Enabled = h.Enabled,
                })
                .ToList(),
        };

        if (vm.Port is int p)
        {
            var status = await _portProbe.ProbeAsync(p, id);
            if (status == PortStatus.UsedByOtherProcess)
            {
                ViewBag.PortWarning = $"Port {p} appears to be in use by another process.";
            }
        }

        return View(vm);
    }

    [HttpPost("Edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ServiceEditViewModel model)
    {
        ViewBag.Presets = _presets.All();
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.DevServices
            .Include(s => s.EnvironmentVariables)
            .Include(s => s.HealthChecks)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null) return NotFound();

        if (await db.DevServices.AnyAsync(s => s.Name == model.Name && s.Id != id))
        {
            ModelState.AddModelError(nameof(model.Name), "A service with this name already exists.");
            return View(model);
        }

        entity.Name = model.Name;
        entity.ServiceType = model.ServiceType;
        entity.WorkingDirectory = model.WorkingDirectory;
        entity.StartCommand = model.StartCommand;
        entity.StartArguments = model.StartArguments;
        entity.StopCommand = model.StopCommand;
        entity.StopArguments = model.StopArguments;
        entity.Url = model.Url;
        entity.Port = model.Port;
        entity.Enabled = model.Enabled;
        entity.AutoStart = model.AutoStart;
        entity.UseExternalInstance = model.UseExternalInstance;
        entity.ExternalPort = model.ExternalPort;
        entity.DisplayOrder = model.DisplayOrder;
        entity.UpdatedUtc = DateTimeOffset.UtcNow;

        SyncEnvironmentVariables(db, entity, model);
        SyncHealthChecks(db, entity, model);

        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Details/{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.DevServices
            .Include(s => s.EnvironmentVariables)
            .Include(s => s.HealthChecks)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null) return NotFound();

        ViewBag.RecentRuns = await db.ServiceRuns
            .Where(r => r.DevServiceId == id)
            .OrderByDescending(r => r.StartedUtc)
            .Take(10)
            .ToListAsync();
        ViewBag.RunningInfo = _manager.GetRunningProcess(id);
        ViewBag.LiveLogs = _manager.GetLiveLogs(id).TakeLast(50).ToList();
        return View(entity);
    }

    [HttpPost("Delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.DevServices.FirstOrDefaultAsync(s => s.Id == id);
        if (entity is null) return NotFound();
        if (_manager.GetRunningProcess(id) is not null)
        {
            TempData["Error"] = "Stop the service before deleting it.";
            return RedirectToAction(nameof(Index));
        }
        db.DevServices.Remove(entity);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int id, CancellationToken cancellationToken)
    {
        var result = await _manager.StartServiceAsync(id, cancellationToken);
        if (WantsJson())
        {
            return Json(new
            {
                success = result.Success,
                serviceId = result.ServiceId,
                runId = result.RunId,
                message = result.Message ?? result.Error,
            });
        }
        TempData[result.Success ? "Info" : "Error"] = result.Message ?? result.Error;
        return RedirectBackOrDashboard();
    }

    [HttpPost("{id:int}/Stop")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(int id, CancellationToken cancellationToken)
    {
        var result = await _manager.StopServiceAsync(id, cancellationToken);
        if (WantsJson())
        {
            return Json(new { success = result.Success, serviceId = id, message = result.Message ?? result.Error });
        }
        TempData[result.Success ? "Info" : "Error"] = result.Message ?? result.Error;
        return RedirectBackOrDashboard();
    }

    [HttpPost("{id:int}/Restart")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restart(int id, CancellationToken cancellationToken)
    {
        var result = await _manager.RestartServiceAsync(id, cancellationToken);
        if (WantsJson())
        {
            return Json(new { success = result.Success, serviceId = id, message = result.Message ?? result.Error });
        }
        TempData[result.Success ? "Info" : "Error"] = result.Message ?? result.Error;
        return RedirectBackOrDashboard();
    }

    [HttpPost("{id:int}/ToggleExternal")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleExternal(int id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.DevServices.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entity is null) return NotFound();

        // Can't passthru a service DevDeck is actively running — stop it first.
        if (!entity.UseExternalInstance && _manager.GetRunningProcess(id) is not null)
        {
            const string msg = "Stop the service before switching it to external (passthru) mode.";
            if (WantsJson()) return Json(new { success = false, serviceId = id, message = msg });
            TempData["Error"] = msg;
            return RedirectBackOrDashboard();
        }

        entity.UseExternalInstance = !entity.UseExternalInstance;
        if (entity.UseExternalInstance && entity.ExternalPort is null)
        {
            entity.ExternalPort = entity.Port ?? 7071;
        }
        entity.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Destination port is derived from the (now-changed) effective port, so rebuild YARP.
        await _proxyProvider.ReloadAsync();

        var info = entity.UseExternalInstance
            ? $"'{entity.Name}' now proxies to an external instance on port {entity.EffectivePort}."
            : $"'{entity.Name}' is back under DevDeck management.";
        if (WantsJson())
        {
            return Json(new { success = true, serviceId = id, useExternalInstance = entity.UseExternalInstance, externalPort = entity.EffectivePort, message = info });
        }
        TempData["Info"] = info;
        return RedirectBackOrDashboard();
    }

    [HttpPost("StartAll")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAll(CancellationToken cancellationToken)
    {
        var result = await _manager.StartAllAsync(cancellationToken);
        TempData["Info"] = $"Started {result.Started} services.";
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost("StopAll")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StopAll(CancellationToken cancellationToken)
    {
        var result = await _manager.StopAllAsync(cancellationToken);
        if (WantsJson())
        {
            return Json(new
            {
                success = result.Outcomes.All(o => o.Success),
                stopped = result.Stopped,
                outcomes = result.Outcomes.Select(o => new
                {
                    serviceId = o.ServiceId,
                    serviceName = o.ServiceName,
                    success = o.Success,
                    message = o.Message,
                }),
            });
        }
        TempData["Info"] = $"Stopped {result.Stopped} services.";
        return RedirectToAction("Index", "Dashboard");
    }

    private bool WantsJson()
    {
        if (string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
            return true;
        var accept = Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export([FromQuery] bool includeSecrets = false, CancellationToken cancellationToken = default)
    {
        var bundle = await _exporter.ExportServicesAsync(includeSecrets, singleId: null, cancellationToken);
        return JsonDownload(bundle, $"devdeck-services-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpGet("Export/{id:int}")]
    public async Task<IActionResult> ExportOne(int id, [FromQuery] bool includeSecrets = false, CancellationToken cancellationToken = default)
    {
        var bundle = await _exporter.ExportServicesAsync(includeSecrets, singleId: id, cancellationToken);
        if (bundle.Services.Count == 0) return NotFound();
        var slug = Slugify(bundle.Services[0].Name);
        return JsonDownload(bundle, $"devdeck-service-{slug}.json");
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

        var result = await _importer.ImportServicesAsync(payload, cancellationToken);
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

    private IActionResult RedirectBackOrDashboard()
    {
        var referer = Request.Headers["Referer"].ToString();
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        {
            return Redirect(uri.PathAndQuery);
        }
        return RedirectToAction("Index", "Dashboard");
    }

    private static void ApplyChildren(DevService entity, ServiceEditViewModel model)
    {
        foreach (var env in model.EnvironmentVariables.Where(r => !r.Delete && !string.IsNullOrWhiteSpace(r.Key)))
        {
            entity.EnvironmentVariables.Add(new ServiceEnvironmentVariable
            {
                Key = env.Key, Value = env.Value ?? string.Empty, IsSecret = env.IsSecret,
            });
        }
        foreach (var hc in model.HealthChecks.Where(r => !r.Delete && !string.IsNullOrWhiteSpace(r.Url)))
        {
            entity.HealthChecks.Add(new ServiceHealthCheck
            {
                Url = hc.Url, ExpectedStatusCode = hc.ExpectedStatusCode,
                IntervalSeconds = hc.IntervalSeconds, Enabled = hc.Enabled,
            });
        }
    }

    private static void SyncEnvironmentVariables(DevDeckDbContext db, DevService entity, ServiceEditViewModel model)
    {
        foreach (var row in model.EnvironmentVariables.Where(r => r.Delete && r.Id != 0))
        {
            var existing = entity.EnvironmentVariables.FirstOrDefault(e => e.Id == row.Id);
            if (existing is not null) db.ServiceEnvironmentVariables.Remove(existing);
        }
        foreach (var row in model.EnvironmentVariables.Where(r => !r.Delete && !string.IsNullOrWhiteSpace(r.Key)))
        {
            if (row.Id == 0)
            {
                entity.EnvironmentVariables.Add(new ServiceEnvironmentVariable
                {
                    Key = row.Key, Value = row.Value ?? string.Empty, IsSecret = row.IsSecret,
                });
            }
            else
            {
                var existing = entity.EnvironmentVariables.FirstOrDefault(e => e.Id == row.Id);
                if (existing is not null)
                {
                    existing.Key = row.Key;
                    if (row.Value != EnvVarEditRow.SecretPlaceholder)
                    {
                        existing.Value = row.Value ?? string.Empty;
                    }
                    existing.IsSecret = row.IsSecret;
                }
            }
        }
    }

    private static void SyncHealthChecks(DevDeckDbContext db, DevService entity, ServiceEditViewModel model)
    {
        foreach (var row in model.HealthChecks.Where(r => r.Delete && r.Id != 0))
        {
            var existing = entity.HealthChecks.FirstOrDefault(h => h.Id == row.Id);
            if (existing is not null) db.ServiceHealthChecks.Remove(existing);
        }
        foreach (var row in model.HealthChecks.Where(r => !r.Delete && !string.IsNullOrWhiteSpace(r.Url)))
        {
            if (row.Id == 0)
            {
                entity.HealthChecks.Add(new ServiceHealthCheck
                {
                    Url = row.Url, ExpectedStatusCode = row.ExpectedStatusCode,
                    IntervalSeconds = row.IntervalSeconds, Enabled = row.Enabled,
                });
            }
            else
            {
                var existing = entity.HealthChecks.FirstOrDefault(h => h.Id == row.Id);
                if (existing is not null)
                {
                    existing.Url = row.Url;
                    existing.ExpectedStatusCode = row.ExpectedStatusCode;
                    existing.IntervalSeconds = row.IntervalSeconds;
                    existing.Enabled = row.Enabled;
                }
            }
        }
    }
}
