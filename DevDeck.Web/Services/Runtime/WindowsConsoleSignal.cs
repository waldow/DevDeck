using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevDeck.Web.Services.Runtime;

/// <summary>
/// Graceful stop for Windows console services. Services are console programs started without
/// a window, so they have no main window to close; the way to ask them to shut down is
/// Ctrl+C on their console. A process can only raise that on a console it is attached to, and
/// detaching DevDeck from its own console would break its logging — so DevDeck starts a
/// short-lived copy of itself in helper mode that attaches to the service's console, raises
/// Ctrl+C there (reaching every process on that console: npm's cmd.exe, node, ...) and exits.
/// </summary>
internal static class WindowsConsoleSignal
{
    public const string HelperFlag = "--devdeck-send-ctrl-c";

    private const uint CTRL_C_EVENT = 0;

    public static bool IsHelperInvocation(string[] args) =>
        args.Length == 2 && string.Equals(args[0], HelperFlag, StringComparison.Ordinal);

    /// <summary>Helper-mode entry point: 0 when Ctrl+C was raised on the target's console.</summary>
    public static int RunHelper(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !uint.TryParse(args[1], out var pid))
        {
            return 2;
        }

        FreeConsole();
        if (!AttachConsole(pid))
        {
            return 3;
        }

        // Ignore the event in this process, then raise it for everything on the console.
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        var raised = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
        // Delivery is asynchronous; stay attached briefly so the console dispatches it.
        Thread.Sleep(200);
        FreeConsole();
        return raised ? 0 : 4;
    }

    /// <summary>Raises Ctrl+C on the console of <paramref name="pid"/>. True when it was delivered.</summary>
    public static bool TrySendCtrlC(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var (fileName, prefixArgs) = HelperCommand();
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in prefixArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(HelperFlag);
        psi.ArgumentList.Add(pid.ToString());

        try
        {
            using var helper = Process.Start(psi);
            if (helper is null) return false;
            if (!helper.WaitForExit(10_000))
            {
                try { helper.Kill(); } catch { /* best-effort */ }
                return false;
            }
            return helper.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // DevDeck's own executable: the apphost (DevDeck.Web.exe) when there is one, otherwise the
    // dotnet host running DevDeck.Web.dll — or, in a single-file publish (no assembly path), the
    // running executable itself.
    private static (string FileName, string[] PrefixArgs) HelperCommand()
    {
        var assemblyPath = typeof(WindowsConsoleSignal).Assembly.Location;
        if (string.IsNullOrEmpty(assemblyPath) && Environment.ProcessPath is { } singleFile)
        {
            return (singleFile, []);
        }

        var appHost = Path.ChangeExtension(assemblyPath, ".exe");
        if (File.Exists(appHost))
        {
            return (appHost, []);
        }

        var processPath = Environment.ProcessPath;
        var host = processPath is not null &&
                   string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";
        return (host, [assemblyPath]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
