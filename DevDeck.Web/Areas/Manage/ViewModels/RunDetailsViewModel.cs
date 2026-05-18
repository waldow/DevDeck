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
