using System.Diagnostics;
using DevDeck.Web.Services.Commands;

namespace DevDeck.Web.Services.Runtime;

/// <summary>
/// Linux: starts services in a session (and process group) of their own via <c>setsid</c>.
/// A child started by Process.Start shares DevDeck's process group, so a Ctrl+C in DevDeck's
/// terminal reached every service at once, behind DevDeck's back; and a service's whole tree
/// can only be signalled as a group if it has a group of its own. (Windows children already
/// get their own hidden console.)
/// </summary>
internal static class ProcessSessions
{
    private static readonly string? SetsidPath = OperatingSystem.IsLinux()
        ? new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists)
        : null;

    /// <summary>
    /// Rewrites <paramref name="psi"/> to run its program through setsid. Only done for a file
    /// the kernel can execute directly — an executable ELF binary, or a script whose #!
    /// interpreter exists — so a command that can't run (missing, not executable, a CRLF
    /// "#!/bin/bash\r" shebang) still fails in Process.Start with a clear FailedToStart, instead
    /// of inside setsid after a "successful" start. Returns whether it did.
    /// </summary>
    public static bool StartInNewSession(ProcessStartInfo psi)
    {
        if (SetsidPath is null || !Path.IsPathRooted(psi.FileName) || !CanExecDirectly(psi.FileName))
        {
            return false;
        }

        if (psi.ArgumentList.Count > 0)
        {
            psi.ArgumentList.Insert(0, psi.FileName);
        }
        else
        {
            psi.Arguments = string.IsNullOrEmpty(psi.Arguments)
                ? CommandLine.Quote(psi.FileName)
                : CommandLine.Quote(psi.FileName) + " " + psi.Arguments;
        }
        psi.FileName = SetsidPath;
        return true;
    }

    internal static bool CanExecDirectly(string path)
    {
        try
        {
            if (!IsExecutableFile(path)) return false;

            Span<byte> head = stackalloc byte[256];
            int read;
            using (var file = File.OpenRead(path))
            {
                read = file.Read(head);
            }
            head = head[..read];

            if (head.StartsWith("\u007fELF"u8)) return true;
            if (!head.StartsWith("#!"u8)) return false;

            // "#!/usr/bin/env node" or "#!/bin/sh -e": the interpreter is the first word; a
            // carriage return (CRLF line ending) is part of it, as it is for the kernel.
            var line = System.Text.Encoding.UTF8.GetString(head[2..]);
            var end = line.IndexOf('\n');
            if (end >= 0) line = line[..end];
            var interpreter = line.TrimStart(' ', '\t').Split(' ', '\t')[0];
            return interpreter.Length > 0 && IsExecutableFile(interpreter);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }
}
