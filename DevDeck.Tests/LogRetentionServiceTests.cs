using DevDeck.Web.Services.Logs;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Tests;

public sealed class LogRetentionServiceTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("devdeck-retention-");

    public void Dispose() => _temp.Delete(recursive: true);

    [Fact]
    public void Sweep_deletes_logs_older_than_cutoff_and_keeps_recent_ones()
    {
        var old = WriteLog("old.log", ageDays: 20);
        var recent = WriteLog("recent.log", ageDays: 1);

        var deleted = LogRetentionService.SweepFolder(_temp.FullName, DateTime.UtcNow.AddDays(-14));

        deleted.Should().Equal(old);
        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
    }

    [Theory]
    [InlineData(9_999_999)]
    [InlineData(int.MaxValue)]
    public void Cutoff_for_huge_retention_clamps_instead_of_throwing(int retentionDays)
    {
        var act = () => LogRetentionService.CutoffUtc(DateTime.UtcNow, retentionDays);

        act.Should().NotThrow();
        act().Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void Cutoff_subtracts_retention_days()
    {
        var now = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        LogRetentionService.CutoffUtc(now, 14).Should().Be(now.AddDays(-14));
    }

    [Fact]
    public void Sweep_ignores_non_log_files()
    {
        var db = Path.Combine(_temp.FullName, "devdeck.db");
        File.WriteAllText(db, "not a log");
        File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddDays(-30));

        var deleted = LogRetentionService.SweepFolder(_temp.FullName, DateTime.UtcNow.AddDays(-14));

        deleted.Should().BeEmpty();
        File.Exists(db).Should().BeTrue();
    }

    [Fact]
    public void Sweep_of_missing_folder_returns_zero()
    {
        var missing = Path.Combine(_temp.FullName, "does-not-exist");

        LogRetentionService.SweepFolder(missing, DateTime.UtcNow).Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_keeps_the_log_of_an_active_run_and_forgets_the_deleted_ones()
    {
        // On Linux, deleting a log a quiet but still-running service writes to would send the
        // rest of its output into an unlinked file; and a run whose log is gone kept offering a
        // download that answered 404.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var factory = new InMemoryFactory(connection);
        var activeLog = WriteLog("active.log", ageDays: 30);
        var finishedLog = WriteLog("finished.log", ageDays: 30);
        long finishedRunId;
        await using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            var service = new DevDeck.Web.Data.Entities.DevService { Name = "svc", ServiceType = "Custom", WorkingDirectory = "/tmp", StartCommand = "x" };
            db.DevServices.Add(service);
            await db.SaveChangesAsync();
            db.ServiceRuns.Add(new DevDeck.Web.Data.Entities.ServiceRun { DevServiceId = service.Id, Status = "Running", LogFilePath = activeLog, StartedUtc = DateTimeOffset.UtcNow.AddDays(-30) });
            var finished = new DevDeck.Web.Data.Entities.ServiceRun { DevServiceId = service.Id, Status = "Stopped", LogFilePath = finishedLog, StartedUtc = DateTimeOffset.UtcNow.AddDays(-30), StoppedUtc = DateTimeOffset.UtcNow.AddDays(-30) };
            db.ServiceRuns.Add(finished);
            await db.SaveChangesAsync();
            finishedRunId = finished.Id;
        }

        var deleted = await LogRetentionService.SweepAsync(factory, _temp.FullName, DateTime.UtcNow.AddDays(-14));

        deleted.Should().Be(1);
        File.Exists(activeLog).Should().BeTrue();
        File.Exists(finishedLog).Should().BeFalse();
        await using var verify = factory.CreateDbContext();
        (await verify.ServiceRuns.FindAsync(finishedRunId))!.LogFilePath.Should().BeNull();
    }

    [Fact]
    public async Task Sweep_keeps_the_azurite_log()
    {
        // Not a run log, so no active run protects it — but a quiet Azurite may still be writing it.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var factory = new InMemoryFactory(connection);
        await using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        var azuriteLog = WriteLog("azurite.log", ageDays: 30);

        var deleted = await LogRetentionService.SweepAsync(factory, _temp.FullName, DateTime.UtcNow.AddDays(-14));

        deleted.Should().Be(0);
        File.Exists(azuriteLog).Should().BeTrue();
    }

    private sealed class InMemoryFactory(Microsoft.Data.Sqlite.SqliteConnection connection)
        : Microsoft.EntityFrameworkCore.IDbContextFactory<DevDeck.Web.Data.DevDeckDbContext>
    {
        public DevDeck.Web.Data.DevDeckDbContext CreateDbContext() =>
            new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DevDeck.Web.Data.DevDeckDbContext>().UseSqlite(connection).Options);
    }

    private string WriteLog(string name, int ageDays)
    {
        var path = Path.Combine(_temp.FullName, name);
        File.WriteAllText(path, "log content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }
}
