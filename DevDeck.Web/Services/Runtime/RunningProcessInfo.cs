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

    /// <summary>
    /// Re-attached at startup: a process an earlier DevDeck session launched and left running.
    /// DevDeck can stop it and sees it exit, but no longer has its output or exit code.
    /// </summary>
    public bool IsAdopted { get; init; }

    /// <summary>Unix: the process group the service runs in when it has one of its own (started via setsid).</summary>
    public int? ProcessGroup { get; set; }
}
