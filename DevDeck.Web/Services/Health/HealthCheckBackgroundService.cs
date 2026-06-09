using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Services.Health;

public sealed class HealthCheckBackgroundService : BackgroundService
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _processManager;
    private readonly PortProbeService _portProbe;
    private readonly CommandTemplateRenderer _renderer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HealthStatusCache _healthStatusCache;
    private readonly ILogger<HealthCheckBackgroundService> _logger;

    public HealthCheckBackgroundService(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager processManager,
        PortProbeService portProbe,
        CommandTemplateRenderer renderer,
        IHttpClientFactory httpClientFactory,
        HealthStatusCache healthStatusCache,
        ILogger<HealthCheckBackgroundService> logger)
    {
        _dbFactory = dbFactory;
        _processManager = processManager;
        _portProbe = portProbe;
        _renderer = renderer;
        _httpClientFactory = httpClientFactory;
        _healthStatusCache = healthStatusCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the host a moment to finish startup migrations.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                _logger.LogWarning(ex, "Health check pass failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(token);
        var checks = await db.ServiceHealthChecks
            .Include(c => c.DevService)
            .Where(c => c.Enabled)
            .ToListAsync(token);

        var client = _httpClientFactory.CreateClient("DevDeck.HealthCheck");
        client.Timeout = TimeSpan.FromSeconds(3);
        var now = DateTimeOffset.UtcNow;

        // Checks run concurrently and each one is isolated: a slow or throwing check must
        // neither push the pass past the polling interval nor lose the other checks'
        // results. Each task mutates only its own tracked entity, so the single
        // SaveChangesAsync below is safe.
        await Task.WhenAll(checks.Where(c => IsDue(c, now)).Select(c => RunCheckAsync(c, client, now, token)));

        await db.SaveChangesAsync(token);
    }

    private static bool IsDue(ServiceHealthCheck check, DateTimeOffset now) =>
        check.LastCheckedUtc is null ||
        now - check.LastCheckedUtc >= TimeSpan.FromSeconds(Math.Max(1, check.IntervalSeconds)) ||
        string.Equals(check.LastStatus, HealthStatusNames.NotRunning, StringComparison.OrdinalIgnoreCase);

    private async Task RunCheckAsync(ServiceHealthCheck check, HttpClient client, DateTimeOffset now, CancellationToken token)
    {
        var service = check.DevService;
        check.LastCheckedUtc = now;

        try
        {
            var url = _renderer.Render(
                check.Url,
                CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.EffectivePort, service.WorkingDirectory)).Text;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Record(check, HealthStatusNames.Unhealthy, null,
                    $"Health check URL is not a valid absolute URL after rendering: '{url}'");
                return;
            }

            var running = _processManager.GetRunningProcess(service.Id);
            var externalEndpointUp = running is null && await _portProbe.IsEndpointOpenAsync(uri.Host, uri.Port, token);
            if (running is null && !externalEndpointUp)
            {
                Record(check, HealthStatusNames.NotRunning, null, null);
                return;
            }

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            var status = (int)response.StatusCode == check.ExpectedStatusCode
                ? HealthStatusNames.Healthy
                : HealthStatusNames.Unhealthy;
            Record(check, status, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Record(check, HealthStatusNames.Timeout, null, "Timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(check, HealthStatusNames.Unhealthy, null, ex.Message);
        }
    }

    private void Record(ServiceHealthCheck check, string status, int? statusCode, string? error)
    {
        check.LastStatus = status;
        check.LastStatusCode = statusCode;
        check.LastError = error;
        _healthStatusCache.Set(check.DevServiceId, check.Id, status);
    }
}
