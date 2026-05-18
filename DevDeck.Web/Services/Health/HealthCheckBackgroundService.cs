using DevDeck.Web.Data;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Services.Health;

public sealed class HealthCheckBackgroundService : BackgroundService
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _processManager;
    private readonly CommandTemplateRenderer _renderer;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HealthCheckBackgroundService> _logger;

    public HealthCheckBackgroundService(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager processManager,
        CommandTemplateRenderer renderer,
        IHttpClientFactory httpClientFactory,
        ILogger<HealthCheckBackgroundService> logger)
    {
        _dbFactory = dbFactory;
        _processManager = processManager;
        _renderer = renderer;
        _httpClientFactory = httpClientFactory;
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

        foreach (var check in checks)
        {
            if (check.LastCheckedUtc is not null &&
                now - check.LastCheckedUtc < TimeSpan.FromSeconds(check.IntervalSeconds))
            {
                continue;
            }

            var service = check.DevService;
            check.LastCheckedUtc = now;

            var running = _processManager.GetRunningProcess(service.Id);
            if (running is null)
            {
                check.LastStatus = HealthStatusNames.NotRunning;
                check.LastStatusCode = null;
                check.LastError = null;
                continue;
            }

            var url = _renderer.Render(
                check.Url,
                CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.Port, service.WorkingDirectory)).Text;

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                check.LastStatusCode = (int)response.StatusCode;
                check.LastStatus = (int)response.StatusCode == check.ExpectedStatusCode
                    ? HealthStatusNames.Healthy
                    : HealthStatusNames.Unhealthy;
                check.LastError = null;
            }
            catch (TaskCanceledException)
            {
                check.LastStatus = HealthStatusNames.Timeout;
                check.LastStatusCode = null;
                check.LastError = "Timed out";
            }
            catch (Exception ex)
            {
                check.LastStatus = HealthStatusNames.Unhealthy;
                check.LastStatusCode = null;
                check.LastError = ex.Message;
            }
        }

        await db.SaveChangesAsync(token);
    }
}
