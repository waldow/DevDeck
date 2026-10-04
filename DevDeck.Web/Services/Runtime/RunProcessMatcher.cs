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
        long? processStartKey,
        Action<Exception>? onInspectFailed = null)
    {
        using var process = TryGetSameRunProcess(processId, runStartedUtc, processStartKey, onInspectFailed);
        return process is not null;
    }

    /// <summary>
    /// The run's process if it is still alive. With a recorded <paramref name="processStartKey"/>
    /// (see <c>ServiceRun.ProcessStartKey</c>) the match is on that; otherwise on the run's start
    /// time — which on Linux breaks when the wall clock is stepped after the start (.NET derives
    /// Process.StartTime from the current boot time, which moves with the clock).
    /// </summary>
    public static Process? TryGetSameRunProcess(
        int processId,
        DateTimeOffset runStartedUtc,
        long? processStartKey,
        Action<Exception>? onInspectFailed = null)
    {
        Process? process = null;
        try
        {
            if (processStartKey is long recorded &&
                (!ProcessTree.TryGetIdentity(processId, out var identity) || !SameStartKey(identity.StartKey, recorded)))
            {
                return null;
            }

            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (processStartKey is not null)
            {
                return process;
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

    // Linux: the kernel's start tick, exact. Elsewhere a creation time read back through
    // DateTime conversions, so allow a little rounding.
    private static bool SameStartKey(long current, long recorded) =>
        OperatingSystem.IsLinux() ? current == recorded : Math.Abs(current - recorded) < TimeSpan.TicksPerSecond;
}
