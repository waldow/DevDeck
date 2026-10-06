using System.Diagnostics;
using DevDeck.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Web.Services.Runtime;

public sealed class RunHistoryRefreshService
{
    private static readonly string[] ActiveStatuses =
    [
        ProcessStatusNames.Starting,
        ProcessStatusNames.Running,
        ProcessStatusNames.Stopping,
    ];

    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly IDevDeckProcessManager _processManager;
    private readonly ILogger<RunHistoryRefreshService> _logger;

    public RunHistoryRefreshService(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        IDevDeckProcessManager processManager,
        ILogger<RunHistoryRefreshService> logger)
    {
        _dbFactory = dbFactory;
        _processManager = processManager;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles ServiceRun rows left in an active state (e.g. after an app restart wiped the
    /// in-memory process dictionary). Best-effort: any failure is logged and swallowed so callers
    /// — including GET requests that render run history — never fault on a reconciliation error.
    /// </summary>
    public async Task<int> RefreshActiveRunsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await RefreshActiveRunsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh active run history; serving existing rows unchanged");
            return 0;
        }
    }

    private async Task<int> RefreshActiveRunsCoreAsync(CancellationToken cancellationToken)
    {
        // Snapshot the tracked runs BEFORE reading rows. The exit handler writes a run's final
        // status and only then drops it from the map, so any run the query below still sees as
        // active is either in this snapshot (and owned by the handler) or genuinely untracked.
        var trackedByRunId = _processManager.GetRunningProcesses()
            .ToDictionary(r => r.ServiceRunId);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var activeRuns = await db.ServiceRuns
            .Where(r => ActiveStatuses.Contains(r.Status))
            .ToListAsync(cancellationToken);

        if (activeRuns.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var run in activeRuns)
        {
            if (trackedByRunId.TryGetValue(run.Id, out var info))
            {
                // Tracked runs are finalized by the process manager's exit handler, and their
                // Stopping is written by the stop path; here only what the start sequence may
                // have failed to record is filled in (Starting -> Running, the PID). An exited or
                // not-yet-started process is left alone. The write is conditional on the run
                // still being open and not stopping, so it can never land on top of a stop or
                // the handler's final status, whichever order the writes commit in.
                if (!IsAlive(info.Process))
                {
                    continue;
                }

                var promote = info.Status == ProcessStatus.Running &&
                              string.Equals(run.Status, ProcessStatusNames.Starting, StringComparison.Ordinal);
                var processId = run.ProcessId ?? TryGetProcessId(info.Process);
                if (promote || (run.ProcessId is null && processId is not null))
                {
                    var status = promote ? ProcessStatusNames.Running : run.Status;
                    changed += await db.ServiceRuns
                        .Where(r => r.Id == run.Id && r.StoppedUtc == null && r.Status != ProcessStatusNames.Stopping)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(r => r.Status, status)
                            .SetProperty(r => r.ProcessId, processId), cancellationToken);
                }
                continue;
            }

            // A start, stop or restart in flight owns its service's run rows — e.g. a run
            // still Starting while Azurite comes up, before its process is tracked — as does a
            // run that became tracked after the snapshot.
            if (_processManager.IsServiceBusy(run.DevServiceId) ||
                _processManager.GetRunningProcess(run.DevServiceId)?.ServiceRunId == run.Id)
            {
                continue;
            }

            if (run.ProcessId is int pid && RunProcessMatcher.IsSameRunProcessStillAlive(
                    pid,
                    run.StartedUtc,
                    run.ProcessStartKey,
                    ex => _logger.LogDebug(ex, "Could not inspect process {ProcessId} while refreshing run history", pid)))
            {
                if (!string.Equals(run.Status, ProcessStatusNames.Running, StringComparison.Ordinal) ||
                    run.StoppedUtc is not null)
                {
                    run.Status = ProcessStatusNames.Running;
                    run.StoppedUtc = null;
                    changed++;
                }
                continue;
            }

            // Conditional on the row still being as read: a start that was in flight at the
            // snapshot may have finished since, its process exited, and its exit handler
            // written the final status (Crashed, say), which must not be overwritten.
            var readStatus = run.Status;
            var finalStatus = CompletedStatus(readStatus);
            var stoppedUtc = run.StoppedUtc ?? now;
            changed += await db.ServiceRuns
                .Where(r => r.Id == run.Id && r.Status == readStatus && r.StoppedUtc == run.StoppedUtc)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Status, finalStatus)
                    .SetProperty(r => r.StoppedUtc, stoppedUtc), cancellationToken);
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static string CompletedStatus(string activeStatus) =>
        activeStatus == ProcessStatusNames.Starting ? ProcessStatusNames.FailedToStart : ProcessStatusNames.Stopped;

    // False for an exited, disposed, or not-yet-started process (HasExited throws for the last).
    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

}
