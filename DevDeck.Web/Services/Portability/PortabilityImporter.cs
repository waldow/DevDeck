using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Services.Portability;

public sealed class PortabilityImporter
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;

    public PortabilityImporter(IDbContextFactory<DevDeckDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PortabilityImportResult> ImportServicesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "services" };
        var bundle = TryParse<PortableServiceBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.DevServices
            .Include(s => s.EnvironmentVariables)
            .Include(s => s.HealthChecks)
            .ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

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
                existing[s.Name] = entity;
                result.Created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<PortabilityImportResult> ImportProfilesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "profiles" };
        var bundle = TryParse<PortableProfileBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LaunchProfiles
            .Include(p => p.Services)
            .ToDictionaryAsync(p => p.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var servicesByName = await db.DevServices.ToDictionaryAsync(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

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
                existing[p.Name] = entity;
                result.Created++;
            }

            ReconcileProfileServices(entity, p, servicesByName, result);
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<PortabilityImportResult> ImportRoutesAsync(string json, CancellationToken cancellationToken = default)
    {
        var result = new PortabilityImportResult { EntityName = "routes" };
        var bundle = TryParse<PortableRouteBundle>(json, result);
        if (bundle is null || !CheckSchema(bundle.SchemaVersion, result)) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ProxyRoutes.ToDictionaryAsync(r => r.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var servicesByName = await db.DevServices.ToDictionaryAsync(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        foreach (var r in bundle.Routes)
        {
            if (string.IsNullOrWhiteSpace(r.Name))
            {
                result.Errors.Add("Skipped a route with an empty name.");
                result.Skipped++;
                continue;
            }

            int? devServiceId = null;
            if (!string.IsNullOrWhiteSpace(r.ServiceName))
            {
                if (servicesByName.TryGetValue(r.ServiceName, out var id))
                {
                    devServiceId = id;
                }
                else
                {
                    result.Warnings.Add($"Route '{r.Name}' references service '{r.ServiceName}' which was not found; route imported without a linked service.");
                }
            }

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
                existing[r.Name] = entity;
                result.Created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return result;
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
                entity.EnvironmentVariables.Add(new ServiceEnvironmentVariable
                {
                    Key = src.Key,
                    Value = value,
                    IsSecret = src.IsSecret,
                });
            }
        }
    }

    private static void MergeHealthChecks(DevService entity, List<PortableHealthCheck> incoming)
    {
        var byUrl = entity.HealthChecks.ToDictionary(h => h.Url, StringComparer.OrdinalIgnoreCase);
        foreach (var src in incoming)
        {
            if (string.IsNullOrWhiteSpace(src.Url)) continue;
            if (byUrl.TryGetValue(src.Url, out var dst))
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

    private static void ReconcileProfileServices(LaunchProfile entity, PortableProfile src, IReadOnlyDictionary<string, int> servicesByName, PortabilityImportResult result)
    {
        var byServiceId = entity.Services.ToDictionary(s => s.DevServiceId);
        foreach (var ps in src.Services)
        {
            if (string.IsNullOrWhiteSpace(ps.ServiceName)) continue;
            if (!servicesByName.TryGetValue(ps.ServiceName, out var serviceId))
            {
                result.Warnings.Add($"Profile '{entity.Name}' references service '{ps.ServiceName}' which was not found; skipped.");
                continue;
            }

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
