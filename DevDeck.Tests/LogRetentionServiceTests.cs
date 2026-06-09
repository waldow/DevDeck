using DevDeck.Web.Services.Logs;
using FluentAssertions;

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

        deleted.Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
    }

    [Fact]
    public void Sweep_ignores_non_log_files()
    {
        var db = Path.Combine(_temp.FullName, "devdeck.db");
        File.WriteAllText(db, "not a log");
        File.SetLastWriteTimeUtc(db, DateTime.UtcNow.AddDays(-30));

        var deleted = LogRetentionService.SweepFolder(_temp.FullName, DateTime.UtcNow.AddDays(-14));

        deleted.Should().Be(0);
        File.Exists(db).Should().BeTrue();
    }

    [Fact]
    public void Sweep_of_missing_folder_returns_zero()
    {
        var missing = Path.Combine(_temp.FullName, "does-not-exist");

        LogRetentionService.SweepFolder(missing, DateTime.UtcNow).Should().Be(0);
    }

    private string WriteLog(string name, int ageDays)
    {
        var path = Path.Combine(_temp.FullName, name);
        File.WriteAllText(path, "log content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }
}
