namespace DevDeck.Web.Data.Entities;

public sealed class ServiceRun
{
    public long Id { get; set; }
    public int DevServiceId { get; set; }
    public DevService DevService { get; set; } = null!;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? StoppedUtc { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
    public required string Status { get; set; }
    public string? StartCommandSnapshot { get; set; }
    public string? StartArgumentsSnapshot { get; set; }
    public string? WorkingDirectorySnapshot { get; set; }
    public string? LogFilePath { get; set; }
    public string? LastError { get; set; }
}
