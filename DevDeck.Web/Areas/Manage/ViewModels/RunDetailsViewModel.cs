using DevDeck.Web.Data.Entities;

namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class RunDetailsViewModel
{
    public ServiceRun Run { get; set; } = null!;
    public string ServiceName { get; set; } = string.Empty;
}

public sealed class RunsListItem
{
    public long Id { get; set; }
    public int DevServiceId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? StoppedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public string? LogFilePath { get; set; }
    public TimeSpan? Duration => StoppedUtc.HasValue ? StoppedUtc - StartedUtc : null;
}

public static class RunTimeFormat
{
    /// <summary>"02:15:07", or "2d 02:15:07" for a run longer than a day ("hh" alone drops the days).</summary>
    public static string Duration(TimeSpan? duration)
    {
        if (duration is not { } d) return "—";
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var clock = d.ToString(@"hh\:mm\:ss");
        return d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {clock}" : clock;
    }

    /// <summary>The stop time in local time; with its date when that isn't the start's date.</summary>
    public static string Stopped(DateTimeOffset startedUtc, DateTimeOffset? stoppedUtc)
    {
        if (stoppedUtc is not { } stopped) return "—";
        var local = stopped.LocalDateTime;
        return local.Date == startedUtc.LocalDateTime.Date
            ? local.ToString("HH:mm:ss")
            : local.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
