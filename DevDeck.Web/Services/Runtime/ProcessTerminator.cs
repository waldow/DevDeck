using System.Diagnostics;

namespace DevDeck.Web.Services.Runtime;

/// <param name="Exited">The root process has exited.</param>
/// <param name="KillIssued">Something had to be force-killed (the root or a leftover descendant).</param>
/// <param name="LeftoversKilled">Descendants that outlived the root and were killed.</param>
internal sealed record StopOutcome(bool Exited, bool KillIssued, int LeftoversKilled);

/// <summary>
/// Stops a whole process tree: a graceful signal to every process in it, a grace period for
/// all of them (not just the root) to exit, then a forced kill of whatever is left. The tree
/// is captured before signalling because once the root exits its descendants are re-parented
/// and can't be found from it any more.
/// </summary>
internal static class ProcessTerminator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    /// <param name="root">The service's process.</param>
    /// <param name="processGroup">Unix: the service's own process group (it was started in a new session), or null.</param>
    /// <param name="grace">How long the tree gets to exit after the graceful signal.</param>
    /// <param name="log">Writes a line to the service's log.</param>
    /// <param name="beforeKill">Called just before anything is force-killed.</param>
    public static async Task<StopOutcome> StopTreeAsync(
        Process root,
        int? processGroup,
        TimeSpan grace,
        Action<string> log,
        Action beforeKill)
    {
        var group = OperatingSystem.IsWindows() ? null : processGroup;
        var rootPid = SafePid(root);
        var rootAlive = rootPid is not null && !SafeHasExited(root);
        if (!rootAlive && !(group is int g && ProcessTree.GetGroupMembers(g).Count > 0))
        {
            return new StopOutcome(true, false, 0);
        }

        // Once the root has exited its descendants can only be found through its group.
        var tree = rootAlive ? ProcessTree.GetDescendants(rootPid!.Value) : [];
        var signalled = rootAlive
            ? SendGracefulSignal(root, rootPid!.Value, tree, group, log)
            : SignalLeftoverGroup(group!.Value, log);
        if (signalled)
        {
            await WaitUntilAsync(() => AllGone(root, tree, group), grace);
        }

        if (AllGone(root, tree, group))
        {
            return new StopOutcome(true, false, 0);
        }

        beforeKill();
        rootAlive = !SafeHasExited(root);
        var leftovers = tree.Where(ProcessTree.IsAlive).ToList();
        log(rootAlive
            ? "Killing process tree"
            : $"Process exited but left {Math.Max(leftovers.Count, 1)} process(es) running; killing them");

        if (rootAlive)
        {
            try { root.Kill(entireProcessTree: true); } catch { /* exited meanwhile */ }
        }
        if (group is int pgid)
        {
            SignalGroup(pgid, PosixSignals.SIGKILL);
        }
        foreach (var leftover in leftovers)
        {
            try
            {
                using var process = Process.GetProcessById(leftover.Pid);
                if (ProcessTree.IsAlive(leftover)) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // exited meanwhile
            }
        }

        await WaitUntilAsync(() => AllGone(root, tree, group), KillWait);
        return new StopOutcome(SafeHasExited(root), true, rootAlive ? 0 : leftovers.Count);
    }

    private static bool SendGracefulSignal(Process root, int rootPid, IReadOnlyList<ProcessIdentity> tree, int? group, Action<string> log)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A GUI program can be asked to close its window; console programs (npm, dotnet,
                // func, docker) have none, so they get Ctrl+C on their console instead.
                if (root.CloseMainWindow())
                {
                    log("Sent close to main window");
                    return true;
                }

                if (!WindowsConsoleSignal.TrySendCtrlC(rootPid))
                {
                    log("Could not send Ctrl+C; killing process tree");
                    return false;
                }

                log("Sent Ctrl+C");
                AnswerBatchPrompt(root);
                return true;
            }

            // Unix: SIGTERM (Process.Kill would be SIGKILL) to every process in the tree, as a
            // terminal's Ctrl+C reaches its whole foreground group — npm alone would only pass
            // it on to its shell, never to the dev server the shell started.
            if (group is int pgid && SignalGroup(pgid, PosixSignals.SIGTERM) > 0)
            {
                log("Sent SIGTERM to the service's process group");
                return true;
            }

            if (!PosixSignals.Send(rootPid, PosixSignals.SIGTERM))
            {
                log($"kill(SIGTERM) failed with errno {PosixSignals.LastError}");
                return false;
            }
            foreach (var descendant in tree)
            {
                if (ProcessTree.IsAlive(descendant)) PosixSignals.Send(descendant.Pid, PosixSignals.SIGTERM);
            }
            log(tree.Count == 0 ? "Sent SIGTERM" : $"Sent SIGTERM to the process and its {tree.Count} child process(es)");
            return true;
        }
        catch (Exception ex)
        {
            log($"Graceful shutdown signal failed: {ex.Message}");
            return false;
        }
    }

    private static bool SignalLeftoverGroup(int group, Action<string> log)
    {
        var sent = SignalGroup(group, PosixSignals.SIGTERM) > 0;
        if (sent) log("Process has exited; sent SIGTERM to what is left of its process group");
        return sent;
    }

    // Each member rather than the group as a whole (kill(-pgid)), so the shared build servers a
    // `dotnet run` service leaves in its group are spared. Returns how many were signalled.
    private static int SignalGroup(int pgid, int signal)
    {
        var signalled = 0;
        foreach (var member in ProcessTree.GetGroupMembers(pgid))
        {
            if (PosixSignals.Send(member.Pid, signal)) signalled++;
        }
        return signalled;
    }

    // npm.cmd, func.cmd, ... run under cmd.exe, which answers Ctrl+C by asking "Terminate batch
    // job (Y/N)?" on stdin once its child exits — and would wait there for the full timeout.
    private static void AnswerBatchPrompt(Process root)
    {
        try
        {
            if (!string.Equals(root.ProcessName, "cmd", StringComparison.OrdinalIgnoreCase)) return;
            root.StandardInput.WriteLine("Y");
            root.StandardInput.Flush();
        }
        catch
        {
            // stdin not redirected (a re-attached process) or already closed
        }
    }

    private static bool AllGone(Process root, IReadOnlyList<ProcessIdentity> tree, int? group) =>
        SafeHasExited(root) &&
        !tree.Any(ProcessTree.IsAlive) &&
        !(group is int pgid && ProcessTree.GetGroupMembers(pgid).Count > 0);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval);
        }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }
}
