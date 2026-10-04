using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevDeck.Web.Services.Runtime;

/// <summary>A process, pinned to one incarnation of its PID by its start time.</summary>
internal readonly record struct ProcessIdentity(int Pid, long StartKey);

/// <summary>
/// Finds the processes a service's process tree is made of. Stopping only the root is not
/// enough: npm runs a dev server through a shell (`npm` → `sh -c vite` → node), and when the
/// root exits its descendants are re-parented and can no longer be found from it — so the tree
/// is captured before anything is signalled.
/// </summary>
internal static class ProcessTree
{
    /// <summary>The live descendants of <paramref name="rootPid"/> (children, grandchildren, ...).</summary>
    public static IReadOnlyList<ProcessIdentity> GetDescendants(int rootPid)
    {
        Dictionary<int, List<int>> children;
        try
        {
            children = ChildrenByParent();
        }
        catch
        {
            return [];
        }

        if (!TryGetIdentity(rootPid, out var root))
        {
            return [];
        }

        var result = new List<ProcessIdentity>();
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<ProcessIdentity>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent.Pid, out var kids)) continue;
            foreach (var pid in kids)
            {
                // A recorded parent PID can outlive the parent and be recycled (Windows does not
                // re-parent orphans), so a real child can't have started before its parent.
                if (!visited.Add(pid) || !TryGetIdentity(pid, out var child) || child.StartKey < parent.StartKey ||
                    IsSharedBuildServer(pid))
                {
                    continue;
                }
                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    public static bool TryGetIdentity(int pid, out ProcessIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsLinux())
        {
            if (!TryReadProcStat(pid, out _, out _, out var startTicks, out var zombie) || zombie) return false;
            identity = new ProcessIdentity(pid, startTicks);
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            identity = new ProcessIdentity(pid, process.StartTime.ToUniversalTime().Ticks);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unix: the live members of process group <paramref name="pgid"/>, minus shared build servers.
    /// Used instead of signalling the group as a whole so those can be spared.
    /// </summary>
    public static IReadOnlyList<ProcessIdentity> GetGroupMembers(int pgid)
    {
        var members = new List<ProcessIdentity>();
        if (OperatingSystem.IsWindows() || pgid <= 1) return members;
        try
        {
            IEnumerable<int> pids = OperatingSystem.IsLinux()
                ? Directory.EnumerateDirectories("/proc")
                    .Select(dir => int.TryParse(Path.GetFileName(dir), out var pid) ? pid : 0)
                    .Where(pid => pid > 0 && TryReadProcStat(pid, out _, out var pgrp, out _, out _) && pgrp == pgid)
                : PsGroupMembers(pgid);
            foreach (var pid in pids)
            {
                if (TryGetIdentity(pid, out var identity) && !IsSharedBuildServer(pid))
                {
                    members.Add(identity);
                }
            }
        }
        catch
        {
            // best effort: the process table changes under us
        }
        return members;
    }

    /// <summary>
    /// The long-lived build servers `dotnet build` / `dotnet run` start and leave running on
    /// purpose — Roslyn's VBCSCompiler, MSBuild's reusable worker nodes, the Razor compiler server.
    /// They are shared by every build of the user's, so stopping a service must not take them
    /// down with it (and they don't answer Ctrl+C, so waiting for them would only stall the stop).
    /// </summary>
    public static bool IsSharedBuildServer(int pid)
    {
        var commandLine = TryGetCommandLine(pid);
        return commandLine is not null &&
               (commandLine.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains("nodeReuse:true", StringComparison.OrdinalIgnoreCase) ||
                (commandLine.Contains("rzc.dll", StringComparison.OrdinalIgnoreCase) &&
                 commandLine.Contains(" server", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? TryGetCommandLine(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            }
            if (OperatingSystem.IsWindows())
            {
                return WindowsProcessSnapshot.CommandLine(pid);
            }
            return RunPs($"-o command= -p {pid}").Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True while that same process (not a later one reusing its PID) is still running.</summary>
    public static bool IsAlive(ProcessIdentity identity) =>
        TryGetIdentity(identity.Pid, out var current) && current.StartKey == identity.StartKey;

    /// <summary>Unix: the process group of <paramref name="pid"/>, or null when unknown.</summary>
    public static int? GetProcessGroup(int pid)
    {
        if (OperatingSystem.IsWindows()) return null;
        if (OperatingSystem.IsLinux())
        {
            return TryReadProcStat(pid, out _, out var pgrp, out _, out _) ? pgrp : null;
        }

        try
        {
            var pgid = PosixSignals.GetProcessGroup(pid);
            return pgid > 0 ? pgid : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<int, List<int>> ChildrenByParent()
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in ParentPairs())
        {
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = new List<int>();
            }
            list.Add(pid);
        }
        return children;
    }

    private static IEnumerable<(int Pid, int ParentPid)> ParentPairs()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(dir), out var pid) &&
                    TryReadProcStat(pid, out var ppid, out _, out _, out _))
                {
                    yield return (pid, ppid);
                }
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var pair in WindowsProcessSnapshot.ParentPairs())
            {
                yield return pair;
            }
        }
        else
        {
            foreach (var pair in PsParentPairs())
            {
                yield return pair;
            }
        }
    }

    // macOS and other Unixes without /proc.
    private static List<(int, int)> PsParentPairs() => PsPairs("-A -o pid= -o ppid=");

    private static IEnumerable<int> PsGroupMembers(int pgid) =>
        PsPairs("-A -o pid= -o pgid=").Where(p => p.Item2 == pgid).Select(p => p.Item1);

    private static List<(int, int)> PsPairs(string arguments)
    {
        var pairs = new List<(int, int)>();
        foreach (var line in RunPs(arguments).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var first) && int.TryParse(parts[1], out var second))
            {
                pairs.Add((first, second));
            }
        }
        return pairs;
    }

    private static string RunPs(string arguments)
    {
        var psi = new ProcessStartInfo("ps", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var ps = Process.Start(psi);
        if (ps is null) return string.Empty;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);
        return output;
    }

    // /proc/<pid>/stat: "pid (comm) state ppid pgrp ... starttime(22nd field) ...". comm may
    // hold spaces or parentheses, so fields are counted from the last ')'.
    private static bool TryReadProcStat(int pid, out int ppid, out int pgrp, out long startTicks, out bool zombie)
    {
        ppid = 0;
        pgrp = 0;
        startTicks = 0;
        zombie = false;
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch
        {
            return false;
        }

        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) return false;
        var fields = text[(close + 2)..].Split(' ');
        if (fields.Length < 20) return false;

        zombie = fields[0] is "Z" or "X";
        return int.TryParse(fields[1], out ppid) &&
               int.TryParse(fields[2], out pgrp) &&
               long.TryParse(fields[19], out startTicks);
    }
}

internal static class PosixSignals
{
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetPgid(int pid);

    public static bool Send(int pid, int signal) => Kill(pid, signal) == 0;

    public static int GetProcessGroup(int pid) => GetPgid(pid);

    public static int LastError => Marshal.GetLastWin32Error();
}

internal static class WindowsProcessSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    /// <summary>The command line of another process (Windows 8.1+), or null when it can't be read.</summary>
    public static string? CommandLine(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0) return null;
                var text = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static List<(int Pid, int ParentPid)> ParentPairs()
    {
        var pairs = new List<(int, int)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero) return pairs;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return pairs;
            do
            {
                pairs.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return pairs;
    }
}
