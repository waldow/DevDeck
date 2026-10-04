namespace DevDeck.Web.Data.Entities;

public sealed class ServiceRun
{
    public long Id { get; set; }
    public int DevServiceId { get; set; }
    public DevService DevService { get; set; } = null!;
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? StoppedUtc { get; set; }
    public int? ProcessId { get; set; }

    /// <summary>
    /// Identifies the process behind <see cref="ProcessId"/> across DevDeck restarts without
    /// relying on the wall clock: on Linux the kernel's start tick (time since boot, which a clock
    /// step — WSL resyncing after the host sleeps — doesn't move, unlike Process.StartTime), elsewhere
    /// the OS's recorded creation time. Null for runs from before it was recorded.
    /// </summary>
    public long? ProcessStartKey { get; set; }
    public int? ExitCode { get; set; }
    public required string Status { get; set; }
    public string? StartCommandSnapshot { get; set; }
    public string? StartArgumentsSnapshot { get; set; }
    public string? WorkingDirectorySnapshot { get; set; }
    public string? LogFilePath { get; set; }
    public string? LastError { get; set; }
}
