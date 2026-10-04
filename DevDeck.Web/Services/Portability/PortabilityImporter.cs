using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Portability;

public sealed class PortabilityImporter
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProxyRouteBuilder _routeBuilder;
    private readonly IOptionsMonitor<DevDeckOptions>? _options;
    private readonly IDevDeckProcessManager? _processManager;
    private readonly AllowedHostsPolicy? _allowedHosts;

    public PortabilityImporter(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProxyRouteBuilder routeBuilder,
        IOptionsMonitor<DevDeckOptions> options,
        IDevDeckProcessManager? processManager = null,
        AllowedHostsPolicy? allowedHosts = null)
    {
        _dbFactory = dbFactory;
        _routeBuilder = routeBuilder;
        _options = options;
        _processManager = processManager;
        _allowedHosts = allowedHosts;
    }

    /// <summary>Test convenience: localhost-only destinations, catch-all routes disabled,
    /// no YARP-level route validation.</summary>
    public PortabilityImporter(IDbContextFactory<DevDeckDbContext> dbFactory, IDevDeckProcessManager? processManager = null)
    {
        _dbFactory = dbFactory;
        _routeBuilder = new ProxyRouteBuilder(new ProxyDestinationValidator(allowExternal: false));
        _options = null;
        _processManager = processManager;
    }

    private bool AllowCatchAllRoutes => _options?.CurrentValue.ReverseProxy.AllowCatchAllRoutes ?? false;

    public async Task<PortabilityImportResult> ImportServicesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "services" };
        var bundle = TryParse<PortableServiceBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;
        var incoming = NonNullEntries(bundle.Services, "service", result);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = new NameLookup<DevService>(
            await db.DevServices
                .Include(s => s.EnvironmentVariables)
                .Include(s => s.HealthChecks)
                .ToListAsync(cancellationToken),
            s => s.Name,
            incoming.Select(s => s.Name));

        foreach (var s in incoming)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                result.Errors.Add("Skipped a service with an empty name.");
                result.Skipped++;
                continue;
            }

            if (ServiceProblems(s) is { Count: > 0 } problems)
            {
                result.Errors.Add($"Skipped service '{s.Name}': {string.Join(" ", problems)}");
                result.Skipped++;
                continue;
            }

            if (existing.TryGetValue(s.Name, out var entity))
            {
                // Same rule as the editor: a managed service that is running (or starting) can't
                // be switched to passthru — its process would keep running with no Stop control.
                var switchesToPassthru = s.UseExternalInstance && !entity.UseExternalInstance;
                var active = _processManager is not null &&
                             (_processManager.GetRunningProcess(entity.Id) is not null || _processManager.IsServiceBusy(entity.Id));
                ApplyServiceScalars(entity, s);
                if (switchesToPassthru && active)
                {
                    entity.UseExternalInstance = false;
                    result.Warnings.Add($"Service '{entity.Name}' is running, so it was left under DevDeck management; stop it and import again to switch it to passthru.");
                }
                entity.UpdatedUtc = DateTimeOffset.UtcNow;
                MergeEnvVars(entity, s.EnvironmentVariables ?? []);
                MergeHealthChecks(entity, s.HealthChecks ?? []);
                result.Updated++;
            }
            else
            {
                entity = new DevService
                {
                    Name = s.Name,
                    ServiceType = s.ServiceType,
                    WorkingDirectory = s.WorkingDirectory,
                    StartCommand = s.StartCommand,
                };
                ApplyServiceScalars(entity, s);
                MergeEnvVars(entity, s.EnvironmentVariables ?? []);
                MergeHealthChecks(entity, s.HealthChecks ?? []);
                db.DevServices.Add(entity);
                existing.AddCreated(s.Name, entity);
                result.Created++;
            }
        }

        await SaveAsync(db, result, cancellationToken);
        return result;
    }

    public async Task<PortabilityImportResult> ImportProfilesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "profiles" };
        var bundle = TryParse<PortableProfileBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;
        var incoming = NonNullEntries(bundle.Profiles, "profile", result);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = new NameLookup<LaunchProfile>(
            await db.LaunchProfiles.Include(p => p.Services).ToListAsync(cancellationToken),
            p => p.Name,
            incoming.Select(p => p.Name));
        var servicesByName = new NameLookup<DevService>(await db.DevServices.ToListAsync(cancellationToken), s => s.Name);

        foreach (var p in incoming)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                result.Errors.Add("Skipped a profile with an empty name.");
                result.Skipped++;
                continue;
            }

            if (ProfileProblems(p) is { Count: > 0 } problems)
            {
                result.Errors.Add($"Skipped profile '{p.Name}': {string.Join(" ", problems)}");
                result.Skipped++;
                continue;
            }

            LaunchProfile entity;
            if (existing.TryGetValue(p.Name, out var existingEntity))
            {
                entity = existingEntity;
                entity.Description = p.Description;
                entity.IsDefault = p.IsDefault;
                entity.DisplayOrder = p.DisplayOrder;
                result.Updated++;
            }
            else
            {
                entity = new LaunchProfile
                {
                    Name = p.Name,
                    Description = p.Description,
                    IsDefault = p.IsDefault,
                    DisplayOrder = p.DisplayOrder,
                };
                db.LaunchProfiles.Add(entity);
                existing.AddCreated(p.Name, entity);
                result.Created++;
            }

            ReconcileProfileServices(entity, p, servicesByName, result);
        }

        await SaveAsync(db, result, cancellationToken);
        return result;
    }

    public async Task<PortabilityImportResult> ImportRoutesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "routes" };
        var bundle = TryParse<PortableRouteBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;
        var incoming = NonNullEntries(bundle.Routes, "route", result);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var allRoutes = await db.ProxyRoutes.ToListAsync(cancellationToken);
        var existing = new NameLookup<ProxyRoute>(allRoutes, r => r.Name, incoming.Select(r => r.Name));
        var servicesByName = new NameLookup<DevService>(await db.DevServices.ToListAsync(cancellationToken), s => s.Name);

        foreach (var r in incoming)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                result.Errors.Add("Skipped a route with an empty name.");
                result.Skipped++;
                continue;
            }

            if (RouteProblems(r) is { Count: > 0 } problems)
            {
                result.Errors.Add($"Skipped route '{r.Name}': {string.Join(" ", problems)}");
                result.Skipped++;
                continue;
            }

            // Mirror the checks the route editor applies. ProxyRouteBuilder would silently
            // skip violating rows when the YARP snapshot is built, so without this an import
            // plants routes that look configured but never work — or worse, reserved-path
            // and external-destination rows that only stay harmless as long as the builder
            // keeps re-checking them.
            if (ReservedPaths.IsReserved(r.MatchPath, out var reservedReason, AllowCatchAllRoutes))
            {
                result.Errors.Add($"Skipped route '{r.Name}': {reservedReason}");
                result.Skipped++;
                continue;
            }

            DevService? service = null;
            if (!string.IsNullOrWhiteSpace(r.ServiceName) && !servicesByName.TryGetValue(r.ServiceName, out service))
            {
                result.Warnings.Add($"Route '{r.Name}' references service '{r.ServiceName}' which was not found; route imported without a linked service.");
            }

            existing.TryGetValue(r.Name, out var entity);
            var candidate = new ProxyRoute { Name = r.Name, MatchPath = r.MatchPath, PathTransformMode = r.PathTransformMode };
            ApplyRouteScalars(candidate, r, service?.Id);
            candidate.Id = entity?.Id ?? 0;
            candidate.DevService = service;

            // Then everything the builder checks — the destination (rendered against the linked
            // service), hosts, constraints and YARP's own validation. Only possible once there
            // is a destination: a route imported without one is skipped by the builder until a
            // service is linked, and the editor validates it fully at that point.
            if (!string.IsNullOrWhiteSpace(r.DestinationUrlOverride) || !string.IsNullOrWhiteSpace(service?.Url))
            {
                var routeErrors = await _routeBuilder.ValidateAsync(candidate);
                if (routeErrors.Count > 0)
                {
                    result.Errors.Add($"Skipped route '{r.Name}': {string.Join(" ", routeErrors)}");
                    result.Skipped++;
                    continue;
                }
            }

            // Against the routes as they will be after this import (an updated route is
            // compared in its new form, through the same entity).
            if (ProxyRouteConflicts.FindConflict(candidate, allRoutes.Where(other => !ReferenceEquals(other, entity))) is { } conflict)
            {
                result.Errors.Add($"Skipped route '{r.Name}': {ProxyRouteConflicts.Describe(conflict)}");
                result.Skipped++;
                continue;
            }

            if (_allowedHosts?.Blocked(ProxyRouteConflicts.ParseHosts(r.MatchHostsCsv)) is { Count: > 0 } blocked)
            {
                result.Warnings.Add($"Route '{r.Name}': requests for {string.Join(", ", blocked.Select(h => $"'{h}'"))} are rejected " +
                                    "because the host is not in DevDeck's AllowedHosts setting.");
            }

            if (entity is not null)
            {
                ApplyRouteScalars(entity, r, service?.Id);
                entity.UpdatedUtc = DateTimeOffset.UtcNow;
                result.Updated++;
            }
            else
            {
                entity = new ProxyRoute
                {
                    Name = r.Name,
                    MatchPath = r.MatchPath,
                    PathTransformMode = r.PathTransformMode,
                };
                ApplyRouteScalars(entity, r, service?.Id);
                db.ProxyRoutes.Add(entity);
                existing.AddCreated(r.Name, entity);
                allRoutes.Add(entity);
                result.Created++;
            }
        }

        await SaveAsync(db, result, cancellationToken);
        return result;
    }

    private static async Task SaveAsync(DevDeckDbContext db, PortabilityImportResult result, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // SaveChanges is transactional, so nothing was applied — reset the counters
            // so the flash message doesn't claim a success that never landed.
            result.Errors.Add($"Import failed to save: {ex.GetBaseException().Message}");
            result.Created = 0;
            result.Updated = 0;
        }
    }

    // The editor's limits (ServiceEditViewModel / ProfileEditViewModel / ProxyRouteEditViewModel
    // and the service editor's env-var key rule): a value the editor refuses would otherwise be
    // stored by an import and then make every later save of that row fail in the editor.
    private static List<string> ServiceProblems(PortableService s)
    {
        var problems = new List<string>();
        if (s.Name.Length > 120) problems.Add("Name is longer than 120 characters.");
        if (string.IsNullOrWhiteSpace(s.ServiceType)) problems.Add("Service type is required.");
        else if (s.ServiceType.Length > 64) problems.Add("Service type is longer than 64 characters.");
        if (string.IsNullOrWhiteSpace(s.WorkingDirectory)) problems.Add("Working directory is required.");
        if (string.IsNullOrWhiteSpace(s.StartCommand)) problems.Add("Start command is required.");
        if (s.Port is < 1 or > 65535) problems.Add($"Port {s.Port} is not between 1 and 65535.");
        if (s.ExternalPort is < 1 or > 65535) problems.Add($"External port {s.ExternalPort} is not between 1 and 65535.");
        if (s.DisplayOrder < 0) problems.Add("Display order must be 0 or greater.");
        foreach (var env in s.EnvironmentVariables ?? [])
        {
            if (env is not null && !string.IsNullOrWhiteSpace(env.Key) && !IsValidEnvVarKey(env.Key))
            {
                problems.Add($"'{env.Key}' is not a valid environment variable name.");
            }
        }
        foreach (var hc in s.HealthChecks ?? [])
        {
            if (hc is null || string.IsNullOrWhiteSpace(hc.Url)) continue;
            if (hc.ExpectedStatusCode is < 100 or > 599) problems.Add($"Health check expected status {hc.ExpectedStatusCode} is not between 100 and 599.");
            if (hc.IntervalSeconds is < 1 or > 86400) problems.Add($"Health check interval {hc.IntervalSeconds}s is not between 1 second and 1 day.");
        }
        return problems;
    }

    private static List<string> ProfileProblems(PortableProfile p)
    {
        var problems = new List<string>();
        if (p.Name.Length > 120) problems.Add("Name is longer than 120 characters.");
        if (p.DisplayOrder < 0) problems.Add("Display order must be 0 or greater.");
        foreach (var ps in p.Services ?? [])
        {
            if (ps is null) continue;
            if (ps.StartOrder < 0) problems.Add($"Start order of '{ps.ServiceName}' must be 0 or greater.");
            if (ps.StartDelaySeconds is < 0 or > 3600) problems.Add($"Start delay of '{ps.ServiceName}' is not between 0 and 3600 seconds.");
        }
        return problems;
    }

    private static List<string> RouteProblems(PortableRoute r)
    {
        var problems = new List<string>();
        if (r.Name.Length > 120) problems.Add("Name is longer than 120 characters.");
        if (string.IsNullOrWhiteSpace(r.MatchPath)) problems.Add("Match path is required.");
        if (string.IsNullOrWhiteSpace(r.PathTransformMode)) problems.Add("Path transform mode is required.");
        if (r.TimeoutSeconds is < 1 or > 600) problems.Add($"Timeout {r.TimeoutSeconds}s is not between 1 and 600 seconds.");
        return problems;
    }

    private static bool IsValidEnvVarKey(string key) =>
        key.Length > 0 && !key.Contains('=') && !key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static void ApplyServiceScalars(DevService entity, PortableService s)
    {
        entity.ServiceType = s.ServiceType;
        entity.WorkingDirectory = s.WorkingDirectory;
        entity.StartCommand = s.StartCommand;
        entity.StartArguments = s.StartArguments;
        entity.StopCommand = s.StopCommand;
        entity.StopArguments = s.StopArguments;
        entity.Url = s.Url;
        entity.Port = s.Port;
        entity.Enabled = s.Enabled;
        entity.AutoStart = s.AutoStart;
        entity.UseExternalInstance = s.UseExternalInstance;
        entity.ExternalPort = s.ExternalPort;
        entity.DisplayOrder = s.DisplayOrder;
    }

    private static void MergeEnvVars(DevService entity, List<PortableEnvVar> incoming)
    {
        var byKey = entity.EnvironmentVariables.ToDictionary(e => e.Key, StringComparer.Ordinal);
        foreach (var src in incoming)
        {
            if (src is null || string.IsNullOrWhiteSpace(src.Key)) continue;
            if (byKey.TryGetValue(src.Key, out var dst))
            {
                // Secret placeholder means "keep what's already in the DB".
                if (!(src.IsSecret && src.Value == EnvVarEditRow.SecretPlaceholder))
                {
                    dst.Value = src.Value ?? string.Empty;
                }
                dst.IsSecret = src.IsSecret;
            }
            else
            {
                var value = src.IsSecret && src.Value == EnvVarEditRow.SecretPlaceholder ? string.Empty : (src.Value ?? string.Empty);
                var added = new ServiceEnvironmentVariable
                {
                    Key = src.Key,
                    Value = value,
                    IsSecret = src.IsSecret,
                };
                entity.EnvironmentVariables.Add(added);
                byKey[src.Key] = added; // a key repeated in the file updates, not re-inserts
            }
        }
    }

    private static void MergeHealthChecks(DevService entity, List<PortableHealthCheck> incoming)
    {
        // The editor allows several checks with the same URL, so pair incoming checks with
        // existing ones one-to-one, in order, rather than assuming URLs are unique.
        var unmatched = entity.HealthChecks
            .GroupBy(h => h.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new Queue<ServiceHealthCheck>(g), StringComparer.OrdinalIgnoreCase);
        foreach (var src in incoming)
        {
            if (src is null || string.IsNullOrWhiteSpace(src.Url)) continue;
            if (unmatched.TryGetValue(src.Url, out var candidates) && candidates.TryDequeue(out var dst))
            {
                dst.ExpectedStatusCode = src.ExpectedStatusCode;
                dst.IntervalSeconds = src.IntervalSeconds;
                dst.Enabled = src.Enabled;
            }
            else
            {
                entity.HealthChecks.Add(new ServiceHealthCheck
                {
                    Url = src.Url,
                    ExpectedStatusCode = src.ExpectedStatusCode,
                    IntervalSeconds = src.IntervalSeconds,
                    Enabled = src.Enabled,
                });
            }
        }
    }

    private static void ReconcileProfileServices(LaunchProfile entity, PortableProfile src, NameLookup<DevService> servicesByName, PortabilityImportResult result)
    {
        // (profile, service) is the key: one entry per service, however often the file (or a
        // case-insensitive match) names it — the last entry wins, as it does for env vars.
        var byServiceId = new Dictionary<int, LaunchProfileService>();
        foreach (var existing in entity.Services)
        {
            byServiceId.TryAdd(existing.DevServiceId, existing);
        }

        foreach (var ps in src.Services ?? [])
        {
            if (ps is null || string.IsNullOrWhiteSpace(ps.ServiceName)) continue;
            if (!servicesByName.TryGetValue(ps.ServiceName, out var service))
            {
                result.Warnings.Add($"Profile '{entity.Name}' references service '{ps.ServiceName}' which was not found; skipped.");
                continue;
            }

            if (byServiceId.TryGetValue(service.Id, out var existing))
            {
                existing.StartOrder = ps.StartOrder;
                existing.StartDelaySeconds = ps.StartDelaySeconds;
            }
            else
            {
                var added = new LaunchProfileService
                {
                    DevServiceId = service.Id,
                    StartOrder = ps.StartOrder,
                    StartDelaySeconds = ps.StartDelaySeconds,
                };
                entity.Services.Add(added);
                byServiceId[service.Id] = added;
            }
        }
    }

    private static void ApplyRouteScalars(ProxyRoute entity, PortableRoute r, int? devServiceId)
    {
        entity.Enabled = r.Enabled;
        entity.DevServiceId = devServiceId;
        entity.DestinationUrlOverride = string.IsNullOrWhiteSpace(r.DestinationUrlOverride) ? null : r.DestinationUrlOverride.Trim();
        entity.MatchPath = r.MatchPath;
        entity.MatchHostsCsv = r.MatchHostsCsv;
        entity.Order = r.Order;
        entity.PathTransformMode = r.PathTransformMode;
        entity.PathPrefixToRemove = r.PathPrefixToRemove;
        entity.PathPrefixToAdd = r.PathPrefixToAdd;
        entity.PathSet = r.PathSet;
        entity.PreserveHostHeader = r.PreserveHostHeader;
        entity.AutoStartService = r.AutoStartService;
        entity.RequireHealthyDestination = r.RequireHealthyDestination;
        entity.TimeoutSeconds = r.TimeoutSeconds;
        entity.AuthorizationPolicy = r.AuthorizationPolicy;
        entity.ShowOnDashboard = r.ShowOnDashboard;
    }

    // An explicit null list ("services": null) or element passes System.Text.Json's `required`
    // check; treat the list as empty and report the elements instead of crashing on them.
    private static List<T> NonNullEntries<T>(IEnumerable<T?>? entries, string kind, PortabilityImportResult result) where T : class
    {
        var list = new List<T>();
        foreach (var entry in entries ?? [])
        {
            if (entry is null)
            {
                result.Errors.Add($"Skipped an empty (null) {kind} entry.");
                result.Skipped++;
                continue;
            }
            list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Name lookup for import merges. The unique indexes compare names case-sensitively
    /// (SQLite BINARY) and profile names are not unique at all, so 'API' and 'api' — or two
    /// 'Dev' profiles — can coexist: prefer an exact match, and fall back to a
    /// case-insensitive one only when that is unambiguous. When the names being imported are
    /// given, the fallback is also refused when the file itself holds several names that differ
    /// only by case, or names the fallback's target exactly — otherwise one entry would be
    /// merged into another's row. Rows created by the import are found by exact name only.
    /// </summary>
    private sealed class NameLookup<T> where T : class
    {
        private readonly Dictionary<string, T> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Name, T Item)>> _folded = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _namesInFile = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _foldedCountInFile = new(StringComparer.OrdinalIgnoreCase);

        public NameLookup(IEnumerable<T> items, Func<T, string> nameOf, IEnumerable<string?>? namesInFile = null)
        {
            foreach (var item in items)
            {
                var name = nameOf(item);
                _exact.TryAdd(name, item);
                if (!_folded.TryGetValue(name, out var sameFolded))
                {
                    _folded[name] = sameFolded = new List<(string, T)>();
                }
                sameFolded.Add((name, item));
            }

            foreach (var name in (namesInFile ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal))
            {
                _namesInFile.Add(name!);
                _foldedCountInFile[name!] = _foldedCountInFile.GetValueOrDefault(name!) + 1;
            }
        }

        public void AddCreated(string name, T item) => _exact.TryAdd(name, item);

        public bool TryGetValue(string name, [NotNullWhen(true)] out T? item)
        {
            if (_exact.TryGetValue(name, out item))
            {
                return true;
            }
            if (_folded.TryGetValue(name, out var sameFolded) && sameFolded.Count == 1 &&
                _foldedCountInFile.GetValueOrDefault(name) <= 1 &&
                !_namesInFile.Contains(sameFolded[0].Name))
            {
                item = sameFolded[0].Item;
                return true;
            }
            item = null;
            return false;
        }
    }

    private static T? TryParse<T>(string json, PortabilityImportResult result) where T : class
    {
        try
        {
            var document = JsonSerializer.Deserialize<T>(json, PortabilityJson.Options);
            if (document is null)
            {
                result.Errors.Add("The JSON document is empty (null).");
            }
            return document;
        }
        catch (JsonException ex)
        {
            result.Errors.Add($"Invalid JSON: {ex.Message}");
            return null;
        }
    }

    private static bool CheckSchema(int schemaVersion, PortabilityImportResult result)
    {
        if (schemaVersion > PortabilityJson.CurrentSchemaVersion)
        {
            result.Errors.Add($"Document was created by a newer DevDeck (schemaVersion {schemaVersion}); this build understands up to {PortabilityJson.CurrentSchemaVersion}.");
            return false;
        }
        return true;
    }
}
