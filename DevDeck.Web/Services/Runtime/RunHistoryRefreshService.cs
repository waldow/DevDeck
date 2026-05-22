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

    public async Task<int> RefreshActiveRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var activeRuns = await db.ServiceRuns
            .Where(r => ActiveStatuses.Contains(r.Status))
            .ToListAsync(cancellationToken);

        if (activeRuns.Count == 0)
        {
            return 0;
        }

        var runningByRunId = _processManager.GetRunningProcesses()
            .ToDictionary(r => r.ServiceRunId);
        var changed = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var run in activeRuns)
        {
            if (runningByRunId.TryGetValue(run.Id, out var info))
            {
                if (TryHasExited(info.Process))
                {
                    changed += CompleteRun(run, now, TryGetExitCode(info.Process), info.KillIssued);
                    continue;
                }

                var refreshedStatus = info.Status.ToString();
                if (!string.Equals(run.Status, refreshedStatus, StringComparison.Ordinal) ||
                    run.StoppedUtc is not null ||
                    run.ProcessId is null)
                {
                    run.Status = refreshedStatus;
                    run.StoppedUtc = null;
                    run.ProcessId ??= TryGetProcessId(info.Process);
                    changed++;
                }
                continue;
            }

            if (run.ProcessId is int pid && IsSameRunProcessStillAlive(pid, run.StartedUtc))
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

            changed += CompleteMissingRun(run, now);
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static int CompleteRun(Data.Entities.ServiceRun run, DateTimeOffset stoppedUtc, int? exitCode, bool killIssued)
    {
        var newStatus = run.Status switch
        {
            ProcessStatusNames.Stopping => killIssued ? ProcessStatusNames.Killed : ProcessStatusNames.Stopped,
            _ => exitCode is 0 ? ProcessStatusNames.Stopped : ProcessStatusNames.Crashed,
        };

        return ApplyCompletion(run, stoppedUtc, newStatus, exitCode);
    }

    private static int CompleteMissingRun(Data.Entities.ServiceRun run, DateTimeOffset stoppedUtc)
    {
        var newStatus = run.Status switch
        {
            ProcessStatusNames.Starting => ProcessStatusNames.FailedToStart,
            _ => ProcessStatusNames.Stopped,
        };

        return ApplyCompletion(run, stoppedUtc, newStatus, run.ExitCode);
    }

    private static int ApplyCompletion(Data.Entities.ServiceRun run, DateTimeOffset stoppedUtc, string status, int? exitCode)
    {
        if (run.StoppedUtc is not null &&
            string.Equals(run.Status, status, StringComparison.Ordinal) &&
            run.ExitCode == exitCode)
        {
            return 0;
        }

        run.StoppedUtc ??= stoppedUtc;
        run.Status = status;
        run.ExitCode = exitCode;
        return 1;
    }

    private static bool TryHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return null;
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

    private bool IsSameRunProcessStillAlive(int processId, DateTimeOffset runStartedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            var processStartedUtc = new DateTimeOffset(process.StartTime).ToUniversalTime();
            return processStartedUtc >= runStartedUtc.AddSeconds(-5);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not inspect process {ProcessId} while refreshing run history", processId);
            return false;
        }
    }
}
