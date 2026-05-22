namespace DevDeck.Web.Services.Runtime;

public enum ProcessStatus
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Crashed = 5,
    FailedToStart = 6,
    Killed = 7,
}

public static class ProcessStatusNames
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Stopping = "Stopping";
    public const string Stopped = "Stopped";
    public const string Crashed = "Crashed";
    public const string FailedToStart = "FailedToStart";
    public const string Killed = "Killed";

    /// <summary>
    /// Maps a live <see cref="ProcessStatus"/> to its canonical persisted name. Unlike
    /// <c>status.ToString()</c>, this never persists a value the UI/filters don't know about:
    /// <see cref="ProcessStatus.Unknown"/> falls back to <see cref="Running"/> (a tracked,
    /// non-exited process is, for run-history purposes, running).
    /// </summary>
    public static string FromStatus(ProcessStatus status) => status switch
    {
        ProcessStatus.Starting => Starting,
        ProcessStatus.Running => Running,
        ProcessStatus.Stopping => Stopping,
        ProcessStatus.Stopped => Stopped,
        ProcessStatus.Crashed => Crashed,
        ProcessStatus.FailedToStart => FailedToStart,
        ProcessStatus.Killed => Killed,
        _ => Running,
    };

    /// <summary>
    /// Resolves the terminal status of a run whose process has exited, given the status it held
    /// while active. Shared by <c>DevDeckProcessManager</c> (live exit handling) and
    /// <c>RunHistoryRefreshService</c> (post-restart reconciliation) so the two never drift.
    /// </summary>
    public static string ResolveExitStatus(string currentStatus, int? exitCode, bool killIssued) => currentStatus switch
    {
        Stopping => killIssued ? Killed : Stopped,
        _ => exitCode is 0 ? Stopped : Crashed,
    };
}
