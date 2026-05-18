using System.Diagnostics;

namespace DevDeck.Web.Services.Runtime;

public sealed class RunningProcessInfo
{
    public required int DevServiceId { get; init; }
    public required long ServiceRunId { get; init; }
    public required string ServiceName { get; init; }
    public required Process Process { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required string LogFilePath { get; init; }
    public ProcessStatus Status { get; set; } = ProcessStatus.Starting;
    public bool KillIssued { get; set; }
    public int? Port { get; init; }
    public string? Url { get; init; }
}
