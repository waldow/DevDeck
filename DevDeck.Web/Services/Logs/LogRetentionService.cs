using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Logs;

/// <summary>
/// Enforces DevDeck:LogRetentionDays — deletes run log files in the logs folder whose
/// last write is older than the retention window. A value of 0 or less disables the sweep.
/// The log of a run that is still active is kept however quiet it has been, and the runs whose
/// log was deleted forget its path, so the UI stops offering a download that would 404.
/// </summary>
public sealed class LogRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly string[] ActiveStatuses =
    [
        ProcessStatusNames.Starting,
        ProcessStatusNames.Running,
        ProcessStatusNames.Stopping,
    ];

    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IOptionsMonitor<DevDeckOptions> options,
        ILogger<LogRetentionService> logger)
    {
        _dbFactory = dbFactory;
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
                try
                {
                    var deleted = await SweepAsync(_dbFactory, DevDeckPaths.LogsFolder, CutoffUtc(DateTime.UtcNow, retentionDays), _logger, stoppingToken);
                    if (deleted > 0)
                    {
                        _logger.LogInformation(
                            "Log retention: deleted {Count} log file(s) older than {Days} day(s)",
                            deleted, retentionDays);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Log retention sweep failed");
                }
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    // A retention window reaching back past DateTime.MinValue would throw (and an exception
    // escaping ExecuteAsync stops the host); clamp it so it simply means "keep everything".
    public static DateTime CutoffUtc(DateTime nowUtc, int retentionDays) =>
        retentionDays >= (nowUtc - DateTime.MinValue).TotalDays
            ? DateTime.MinValue
            : nowUtc.AddDays(-retentionDays);

    /// <summary>
    /// One sweep: deletes old run logs except those of active runs and Azurite's (on Linux a
    /// deleted log a running process still writes to would swallow its output), then clears the log path of
    /// the finished runs whose file is gone. Returns how many files were deleted.
    /// </summary>
    public static async Task<int> SweepAsync(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        string folder,
        DateTime deleteOlderThanUtc,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var activeLogs = (await db.ServiceRuns
                .Where(r => ActiveStatuses.Contains(r.Status) && r.LogFilePath != null)
                .Select(r => r.LogFilePath!)
                .ToListAsync(cancellationToken))
            .Select(Path.GetFullPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        // Not a run log: a long-running Azurite may not have written to it for days.
        activeLogs.Add(Path.GetFullPath(Path.Combine(folder, Path.GetFileName(AzuriteSupervisor.LogFile))));

        var deleted = SweepFolder(folder, deleteOlderThanUtc, logger, activeLogs);
        if (deleted.Count > 0)
        {
            await db.ServiceRuns
                .Where(r => r.StoppedUtc != null && r.LogFilePath != null && deleted.Contains(r.LogFilePath))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.LogFilePath, (string?)null), cancellationToken);
        }
        return deleted.Count;
    }

    /// <summary>Deletes the *.log files older than the cutoff, except <paramref name="keep"/>; returns their paths.</summary>
    public static IReadOnlyList<string> SweepFolder(
        string folder, DateTime deleteOlderThanUtc, ILogger? logger = null, IReadOnlySet<string>? keep = null)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        var deleted = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < deleteOlderThanUtc && keep?.Contains(Path.GetFullPath(file)) != true)
                {
                    File.Delete(file);
                    deleted.Add(file);
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
