using DevDeck.Web.Data;
using DevDeck.Web.Options;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Logs;

/// <summary>
/// Enforces DevDeck:LogRetentionDays — deletes run log files in the logs folder whose
/// last write is older than the retention window. A value of 0 or less disables the sweep.
/// </summary>
public sealed class LogRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(IOptionsMonitor<DevDeckOptions> options, ILogger<LogRetentionService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, auto-start) settle before touching the disk.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var retentionDays = _options.CurrentValue.LogRetentionDays;
            if (retentionDays > 0)
            {
                var deleted = SweepFolder(DevDeckPaths.LogsFolder, DateTime.UtcNow.AddDays(-retentionDays), _logger);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Log retention: deleted {Count} log file(s) older than {Days} day(s)",
                        deleted, retentionDays);
                }
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    public static int SweepFolder(string folder, DateTime deleteOlderThanUtc, ILogger? logger = null)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < deleteOlderThanUtc)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still held open (e.g. by a long-running service) is skipped
                // this sweep and picked up by a later one.
                logger?.LogDebug(ex, "Log retention: could not delete {File}", file);
            }
        }
        return deleted;
    }
}
