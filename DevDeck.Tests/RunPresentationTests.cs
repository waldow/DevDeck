using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Services.Proxy;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class RunPresentationTests
{
    [Fact]
    public void A_duration_over_a_day_keeps_its_days()
    {
        // "hh" is the 0-23 hours component, so a two-day run used to read 02:00:00.
        RunTimeFormat.Duration(TimeSpan.FromHours(50)).Should().Be("2d 02:00:00");
        RunTimeFormat.Duration(new TimeSpan(0, 1, 2, 3)).Should().Be("01:02:03");
        RunTimeFormat.Duration(null).Should().Be("—");
    }

    [Fact]
    public void A_stop_on_another_day_shows_its_date()
    {
        var started = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

        RunTimeFormat.Stopped(started, started.AddMinutes(5)).Should().NotContain("-");
        RunTimeFormat.Stopped(started, started.AddDays(2)).Should().Be(started.AddDays(2).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        RunTimeFormat.Stopped(started, null).Should().Be("—");
    }
}

// UseStaticFiles runs before routing, so any file served from the web root silently wins over a
// proxy route for that path — e.g. a proxied app's /favicon-32x32.png. Every static asset must
// therefore live under a reserved prefix (which proxy routes can't use).
public sealed class StaticAssetPlacementTests
{
    [Fact]
    public void Every_static_asset_is_under_a_reserved_path()
    {
        var webRoot = Path.Combine(RepositoryRoot(), "DevDeck.Web", "wwwroot");

        var unreserved = Directory.EnumerateFileSystemEntries(webRoot)
            .Select(entry => "/" + Path.GetFileName(entry))
            .Where(path => !ReservedPaths.IsReservedRequestPath(path))
            .ToList();

        unreserved.Should().BeEmpty();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevDeck.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root (DevDeck.slnx) not found.");
    }
}
