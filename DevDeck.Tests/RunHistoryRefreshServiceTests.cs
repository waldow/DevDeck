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

    public RunHistoryRefreshServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Refresh_marks_stale_running_run_as_stopped()
    {
        var runId = await SeedRunAsync(ProcessStatusNames.Running);
        var refresh = new RunHistoryRefreshService(_factory, new FakeProcessManager(), NullLogger<RunHistoryRefreshService>.Instance);

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
        var refresh = new RunHistoryRefreshService(_factory, new FakeProcessManager(), NullLogger<RunHistoryRefreshService>.Instance);

        await refresh.RefreshActiveRunsAsync();

        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.FailedToStart);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_leaves_completed_runs_unchanged()
    {
        var stoppedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var runId = await SeedRunAsync(ProcessStatusNames.Stopped, stoppedUtc);
        var refresh = new RunHistoryRefreshService(_factory, new FakeProcessManager(), NullLogger<RunHistoryRefreshService>.Instance);

        var changed = await refresh.RefreshActiveRunsAsync();

        changed.Should().Be(0);
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Stopped);
        run.StoppedUtc.Should().BeCloseTo(stoppedUtc, TimeSpan.FromMilliseconds(1));
    }

    private async Task<long> SeedRunAsync(string status, DateTimeOffset? stoppedUtc = null)
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
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            StoppedUtc = stoppedUtc,
            Status = status,
        };
        db.ServiceRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private sealed class FakeProcessManager : IDevDeckProcessManager
    {
        public Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StopServiceResult { ServiceId = serviceId, Success = false });

        public Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new RestartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartProfileResult { ProfileId = profileId, Success = false });

        public Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StopAllResult());

        public RunningProcessInfo? GetRunningProcess(int serviceId) => null;

        public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => [];

        public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => [];

        public void ClearLiveLogs(int serviceId)
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
