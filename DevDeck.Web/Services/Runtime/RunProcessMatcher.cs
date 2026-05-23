using System.Diagnostics;

namespace DevDeck.Web.Services.Runtime;

internal static class RunProcessMatcher
{
    // A live PID is not enough: the OS can recycle it. DevDeck stamps run.StartedUtc
    // immediately before spawning, so the genuine process StartTime should sit in this
    // small window around the run row's start time.
    private static readonly TimeSpan ProcessStartLowerSlack = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStartUpperSlack = TimeSpan.FromSeconds(60);

    public static bool IsSameRunProcessStillAlive(
        int processId,
        DateTimeOffset runStartedUtc,
        Action<Exception>? onInspectFailed = null)
    {
        using var process = TryGetSameRunProcess(processId, runStartedUtc, onInspectFailed);
        return process is not null;
    }

    public static Process? TryGetSameRunProcess(
        int processId,
        DateTimeOffset runStartedUtc,
        Action<Exception>? onInspectFailed = null)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            var processStartedUtc = new DateTimeOffset(process.StartTime).ToUniversalTime();
            var isSameRun =
                processStartedUtc >= runStartedUtc - ProcessStartLowerSlack &&
                processStartedUtc <= runStartedUtc + ProcessStartUpperSlack;

            if (isSameRun)
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            onInspectFailed?.Invoke(ex);
            return null;
        }
    }
}
