using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Services.Portability;

public sealed class PortabilityExporter
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;

    public PortabilityExporter(IDbContextFactory<DevDeckDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PortableServiceBundle> ExportServicesAsync(bool includeSecrets, int? singleId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DevServices
            .Include(s => s.EnvironmentVariables)
            .Include(s => s.HealthChecks)
            .AsNoTracking()
            .AsQueryable();
        if (singleId is int id) query = query.Where(s => s.Id == id);

        var rows = await query.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync(cancellationToken);
        return new PortableServiceBundle
        {
            Services = rows.Select(s => new PortableService
            {
                Name = s.Name,
                ServiceType = s.ServiceType,
                WorkingDirectory = s.WorkingDirectory,
                StartCommand = s.StartCommand,
                StartArguments = s.StartArguments,
                StopCommand = s.StopCommand,
                StopArguments = s.StopArguments,
                Url = s.Url,
                Port = s.Port,
                Enabled = s.Enabled,
                AutoStart = s.AutoStart,
                DisplayOrder = s.DisplayOrder,
                EnvironmentVariables = s.EnvironmentVariables
                    .OrderBy(e => e.Key)
                    .Select(e => new PortableEnvVar
                    {
                        Key = e.Key,
                        Value = e.IsSecret && !includeSecrets ? EnvVarEditRow.SecretPlaceholder : e.Value,
                        IsSecret = e.IsSecret,
                    })
                    .ToList(),
                HealthChecks = s.HealthChecks
                    .Select(h => new PortableHealthCheck
                    {
                        Url = h.Url,
                        ExpectedStatusCode = h.ExpectedStatusCode,
                        IntervalSeconds = h.IntervalSeconds,
                        Enabled = h.Enabled,
                    })
                    .ToList(),
            }).ToList(),
        };
    }

    public async Task<PortableProfileBundle> ExportProfilesAsync(int? singleId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.LaunchProfiles
            .Include(p => p.Services)
            .ThenInclude(ps => ps.DevService)
            .AsNoTracking()
            .AsQueryable();
        if (singleId is int id) query = query.Where(p => p.Id == id);

        var rows = await query.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).ToListAsync(cancellationToken);
        return new PortableProfileBundle
        {
            Profiles = rows.Select(p => new PortableProfile
            {
                Name = p.Name,
                Description = p.Description,
                IsDefault = p.IsDefault,
                DisplayOrder = p.DisplayOrder,
                Services = p.Services
                    .OrderBy(s => s.StartOrder)
                    .Select(s => new PortableProfileService
                    {
                        ServiceName = s.DevService.Name,
                        StartOrder = s.StartOrder,
                        StartDelaySeconds = s.StartDelaySeconds,
                    })
                    .ToList(),
            }).ToList(),
        };
    }

    public async Task<PortableRouteBundle> ExportRoutesAsync(int? singleId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ProxyRoutes
            .Include(r => r.DevService)
            .AsNoTracking()
            .AsQueryable();
        if (singleId is int id) query = query.Where(r => r.Id == id);

        var rows = await query.OrderBy(r => r.Order).ThenBy(r => r.Name).ToListAsync(cancellationToken);
        return new PortableRouteBundle
        {
            Routes = rows.Select(r => new PortableRoute
            {
                Name = r.Name,
                Enabled = r.Enabled,
                ServiceName = r.DevService?.Name,
                DestinationUrlOverride = r.DestinationUrlOverride,
                MatchPath = r.MatchPath,
                MatchHostsCsv = r.MatchHostsCsv,
                Order = r.Order,
                PathTransformMode = r.PathTransformMode,
                PathPrefixToRemove = r.PathPrefixToRemove,
                PathPrefixToAdd = r.PathPrefixToAdd,
                PathSet = r.PathSet,
                PreserveHostHeader = r.PreserveHostHeader,
                AutoStartService = r.AutoStartService,
                RequireHealthyDestination = r.RequireHealthyDestination,
                TimeoutSeconds = r.TimeoutSeconds,
                AuthorizationPolicy = r.AuthorizationPolicy,
                ShowOnDashboard = r.ShowOnDashboard,
            }).ToList(),
        };
    }
}
