using System.Diagnostics;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDeck.Tests;

public sealed class RunHistoryRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly List<Process> _spawned = new();

    public RunHistoryRefreshServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var process in _spawned)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
            process.Dispose();
        }
        _connection.Dispose();
    }

    private RunHistoryRefreshService CreateService(IDevDeckProcessManager processManager) =>
        new(_factory, processManager, NullLogger<RunHistoryRefreshService>.Instance);

    // --- runs with no tracked process and no live PID (post-restart, child gone) ---

    [Fact]
    public async Task Refresh_marks_stale_running_run_as_stopped()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Running);
        var refresh = CreateService(new FakeProcessManager());

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Stopped);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_marks_stale_starting_run_as_failed_to_start()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Starting);
        var refresh = CreateService(new FakeProcessManager());

        await refresh.RefreshActiveRunsAsync();

        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.FailedToStart);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_ignores_non_active_runs()
    {
        var stoppedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var runId = await SeedRunAsync(ProcessStatusNames.Stopped, stoppedUtc);
        var refresh = CreateService(new FakeProcessManager());

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(0);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Stopped);
        run.StoppedUtc.Should().BeCloseTo(stoppedUtc, TimeSpan.FromMilliseconds(1));
    }

    // --- runs whose process is still tracked in memory ---

    [Fact]
    public async Task Refresh_marks_running_run_crashed_when_tracked_process_exited_nonzero()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Running);
        var process = StartExitedProcess(exitCode: 1);
        var refresh = CreateService(new FakeProcessManager(Info(runId, process, ProcessStatus.Running)));

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Crashed);
        run.ExitCode.Should().Be(1);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_marks_stopping_run_killed_when_tracked_process_exited_after_kill()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Stopping);
        var process = StartExitedProcess(exitCode: 1);
        var refresh = CreateService(new FakeProcessManager(Info(runId, process, ProcessStatus.Stopping, killIssued: true)));

        await refresh.RefreshActiveRunsAsync();

        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Killed);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_backfills_process_id_for_live_tracked_run()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Starting);
        var process = StartLiveProcess();
        var refresh = CreateService(new FakeProcessManager(Info(runId, process, ProcessStatus.Running)));

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Running);
        run.ProcessId.Should().Be(process.Id);
        run.StoppedUtc.Should().BeNull();
    }

    // --- runs reconciled purely from a stored PID (process untracked after restart) ---

    [Fact]
    public async Task Refresh_keeps_run_running_when_stored_pid_is_still_alive()
    {
        var process = StartLiveProcess();
        var runId = await SeedRunAsync(ProcessStatusNames.Starting, processId: process.Id, startedUtc: DateTimeOffset.UtcNow);
        var refresh = CreateService(new FakeProcessManager());

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Running);
        run.StoppedUtc.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_completes_run_when_live_pid_started_long_after_run_start()
    {
        // Simulates PID reuse: a live process holds the stored PID, but it started an hour after
        // this run did, so it cannot be our process. The run must be completed, not kept Running.
        var process = StartLiveProcess();
        var runId = await SeedRunAsync(
            ProcessStatusNames.Running,
            processId: process.Id,
            startedUtc: DateTimeOffset.UtcNow.AddHours(-1));
        var refresh = CreateService(new FakeProcessManager());

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Stopped);
        run.StoppedUtc.Should().NotBeNull();
    }

    private async Task<long> SeedRunAsync(string status, DateTimeOffset? stoppedUtc = null, int? processId = null, DateTimeOffset? startedUtc = null)
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = Guid.NewGuid().ToString("N"),
            ServiceType = "Custom",
            WorkingDirectory = "C:\\src\\test",
            StartCommand = "dotnet",
        };
        db.DevServices.Add(service);
        await db.SaveChangesAsync();

        var run = new ServiceRun
        {
            DevServiceId = service.Id,
            StartedUtc = startedUtc ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            StoppedUtc = stoppedUtc,
            ProcessId = processId,
            Status = status,
        };
        db.ServiceRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static RunningProcessInfo Info(long runId, Process process, ProcessStatus status, bool killIssued = false) =>
        new()
        {
            DevServiceId = 1,
            ServiceRunId = runId,
            ServiceName = "svc",
            Process = process,
            StartedUtc = DateTimeOffset.UtcNow,
            LogFilePath = "run.log",
            Status = status,
            KillIssued = killIssued,
        };

    private Process StartLiveProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c pause")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        return process;
    }

    private Process StartExitedProcess(int exitCode)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c exit {exitCode}")
            : new ProcessStartInfo("/bin/sh", $"-c \"exit {exitCode}\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var process = Process.Start(psi)!;
        _spawned.Add(process);
        process.WaitForExit(5000);
        return process;
    }

    private sealed class FakeProcessManager : IDevDeckProcessManager
    {
        private readonly IReadOnlyCollection<RunningProcessInfo> _running;

        public FakeProcessManager(params RunningProcessInfo[] running) => _running = running;

        public Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StopServiceResult { ServiceId = serviceId, Success = false });

        public Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new RestartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartProfileResult { ProfileId = profileId, Success = false });

        public Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartAllResult());

        public Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StopAllResult());

        public RunningProcessInfo? GetRunningProcess(int serviceId) => null;

        public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => _running;

        public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => [];

        public void ClearLiveLogs(int serviceId)
        {
        }

        public void AppendProxyLog(int serviceId, string text)
        {
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DevDeckDbContext>
    {
        private readonly DbContextOptions<DevDeckDbContext> _options;

        public TestDbContextFactory(SqliteConnection connection)
        {
            _options = new DbContextOptionsBuilder<DevDeckDbContext>().UseSqlite(connection).Options;
        }

        public DevDeckDbContext CreateDbContext() => new(_options);
    }
}
