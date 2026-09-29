using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Portability;

public sealed class PortabilityImporter
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProxyDestinationValidator _destinationValidator;
    private readonly ProxyRouteBuilder _routeBuilder;
    private readonly IOptionsMonitor<DevDeckOptions>? _options;

    public PortabilityImporter(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProxyDestinationValidator destinationValidator,
        ProxyRouteBuilder routeBuilder,
        IOptionsMonitor<DevDeckOptions> options)
    {
        _dbFactory = dbFactory;
        _destinationValidator = destinationValidator;
        _routeBuilder = routeBuilder;
        _options = options;
    }

    /// <summary>Test convenience: localhost-only destinations, catch-all routes disabled,
    /// no YARP-level route validation.</summary>
    public PortabilityImporter(IDbContextFactory<DevDeckDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _destinationValidator = new ProxyDestinationValidator(allowExternal: false);
        _routeBuilder = new ProxyRouteBuilder(_destinationValidator);
        _options = null;
    }

    private static readonly CommandTemplateRenderer Renderer = new();

    private bool AllowCatchAllRoutes => _options?.CurrentValue.ReverseProxy.AllowCatchAllRoutes ?? false;

    public async Task<PortabilityImportResult> ImportServicesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "services" };
        var bundle = TryParse<PortableServiceBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = new NameLookup<DevService>(
            await db.DevServices
                .Include(s => s.EnvironmentVariables)
                .Include(s => s.HealthChecks)
                .ToListAsync(cancellationToken),
            s => s.Name);

        foreach (var s in bundle.Services)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                result.Errors.Add("Skipped a service with an empty name.");
                result.Skipped++;
                continue;
            }

            if (existing.TryGetValue(s.Name, out var entity))
            {
                ApplyServiceScalars(entity, s);
                entity.UpdatedUtc = DateTimeOffset.UtcNow;
                MergeEnvVars(entity, s.EnvironmentVariables);
                MergeHealthChecks(entity, s.HealthChecks);
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
                MergeEnvVars(entity, s.EnvironmentVariables);
                MergeHealthChecks(entity, s.HealthChecks);
                db.DevServices.Add(entity);
                existing.Add(s.Name, entity);
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = new NameLookup<LaunchProfile>(
            await db.LaunchProfiles.Include(p => p.Services).ToListAsync(cancellationToken),
            p => p.Name);
        var servicesByName = new NameLookup<DevService>(await db.DevServices.ToListAsync(cancellationToken), s => s.Name);

        foreach (var p in bundle.Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                result.Errors.Add("Skipped a profile with an empty name.");
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
                existing.Add(p.Name, entity);
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = new NameLookup<ProxyRoute>(await db.ProxyRoutes.ToListAsync(cancellationToken), r => r.Name);
        var servicesByName = new NameLookup<DevService>(await db.DevServices.ToListAsync(cancellationToken), s => s.Name);

        foreach (var r in bundle.Routes)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                result.Errors.Add("Skipped a route with an empty name.");
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

            var destinationError = ValidateRouteDestination(r.DestinationUrlOverride, service);
            if (destinationError is not null)
            {
                result.Errors.Add($"Skipped route '{r.Name}': {destinationError}");
                result.Skipped++;
                continue;
            }

            // Then the rest of what the builder checks, YARP's own validation included (hosts,
            // authorization policy, ...). Only possible once there is a destination: a route
            // imported without one is skipped by the builder until a service is linked, and
            // the editor validates it fully at that point.
            if (!string.IsNullOrWhiteSpace(r.DestinationUrlOverride) || !string.IsNullOrWhiteSpace(service?.Url))
            {
                var candidate = new ProxyRoute { Name = r.Name, MatchPath = r.MatchPath, PathTransformMode = r.PathTransformMode };
                ApplyRouteScalars(candidate, r, service?.Id);
                candidate.DevService = service;
                var routeErrors = await _routeBuilder.ValidateAsync(candidate);
                if (routeErrors.Count > 0)
                {
                    result.Errors.Add($"Skipped route '{r.Name}': {string.Join(" ", routeErrors)}");
                    result.Skipped++;
                    continue;
                }
            }

            int? devServiceId = service?.Id;

            if (existing.TryGetValue(r.Name, out var entity))
            {
                ApplyRouteScalars(entity, r, devServiceId);
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
                ApplyRouteScalars(entity, r, devServiceId);
                db.ProxyRoutes.Add(entity);
                existing.Add(r.Name, entity);
                result.Created++;
            }
        }

        await SaveAsync(db, result, cancellationToken);
        return result;
    }

    // Same rule as the route editor and ProxyRouteBuilder: the destination is the override,
    // else the linked service's URL, with {port}/{id}/... rendered against that service
    // before it is validated (a templated "http://localhost:{port}" is not a parseable URL).
    private string? ValidateRouteDestination(string? destinationOverride, DevService? service)
    {
        var destination = !string.IsNullOrWhiteSpace(destinationOverride) ? destinationOverride.Trim() : service?.Url;
        if (string.IsNullOrWhiteSpace(destination)) return null;

        if (service is not null)
        {
            var rendered = Renderer.Render(
                destination,
                CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.EffectivePort, service.WorkingDirectory));
            if (rendered.UnknownPlaceholders.Count > 0)
            {
                return $"Destination URL has unknown placeholder(s): {string.Join(", ", rendered.UnknownPlaceholders)}.";
            }
            destination = rendered.Text;
        }

        var validation = _destinationValidator.Validate(destination);
        return validation.IsValid ? null : validation.Error;
    }

    private static async Task SaveAsync(DevDeckDbContext db, PortabilityImportResult result, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // SaveChanges is transactional, so nothing was applied — reset the counters
            // so the flash message doesn't claim a success that never landed.
            result.Errors.Add($"Import failed to save: {ex.GetBaseException().Message}");
            result.Created = 0;
            result.Updated = 0;
        }
    }

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
            if (string.IsNullOrWhiteSpace(src.Key)) continue;
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
            if (string.IsNullOrWhiteSpace(src.Url)) continue;
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
        var byServiceId = entity.Services.ToDictionary(s => s.DevServiceId);
        foreach (var ps in src.Services)
        {
            if (string.IsNullOrWhiteSpace(ps.ServiceName)) continue;
            if (!servicesByName.TryGetValue(ps.ServiceName, out var service))
            {
                result.Warnings.Add($"Profile '{entity.Name}' references service '{ps.ServiceName}' which was not found; skipped.");
                continue;
            }

            var serviceId = service.Id;
            if (byServiceId.TryGetValue(serviceId, out var existing))
            {
                existing.StartOrder = ps.StartOrder;
                existing.StartDelaySeconds = ps.StartDelaySeconds;
            }
            else
            {
                entity.Services.Add(new LaunchProfileService
                {
                    DevServiceId = serviceId,
                    StartOrder = ps.StartOrder,
                    StartDelaySeconds = ps.StartDelaySeconds,
                });
            }
        }
    }

    private static void ApplyRouteScalars(ProxyRoute entity, PortableRoute r, int? devServiceId)
    {
        entity.Enabled = r.Enabled;
        entity.DevServiceId = devServiceId;
        entity.DestinationUrlOverride = r.DestinationUrlOverride;
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

    /// <summary>
    /// Name lookup for import merges. The unique indexes compare names case-sensitively
    /// (SQLite BINARY) and profile names are not unique at all, so 'API' and 'api' — or two
    /// 'Dev' profiles — can coexist: prefer an exact match, and fall back to a
    /// case-insensitive one only when that is unambiguous.
    /// </summary>
    private sealed class NameLookup<T> where T : class
    {
        private readonly Dictionary<string, T> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<T>> _folded = new(StringComparer.OrdinalIgnoreCase);

        public NameLookup(IEnumerable<T> items, Func<T, string> nameOf)
        {
            foreach (var item in items)
            {
                Add(nameOf(item), item);
            }
        }

        public void Add(string name, T item)
        {
            _exact.TryAdd(name, item);
            if (!_folded.TryGetValue(name, out var sameFolded))
            {
                _folded[name] = sameFolded = new List<T>();
            }
            sameFolded.Add(item);
        }

        public bool TryGetValue(string name, [NotNullWhen(true)] out T? item)
        {
            if (_exact.TryGetValue(name, out item))
            {
                return true;
            }
            if (_folded.TryGetValue(name, out var sameFolded) && sameFolded.Count == 1)
            {
                item = sameFolded[0];
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
            return JsonSerializer.Deserialize<T>(json, PortabilityJson.Options);
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
