using System.Runtime.InteropServices;

namespace DevDeck.Web.Services.Commands;

public sealed class CommandExecutableResolver
{
    private readonly bool _isWindows;
    private static readonly char[] DirectorySeparators = ['\\', '/'];

    public CommandExecutableResolver()
        : this(RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
    }

    public CommandExecutableResolver(bool isWindows)
    {
        _isWindows = isWindows;
    }

    public string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        if (Path.IsPathRooted(command))
        {
            return command;
        }

        var name = command.Trim();

        if (_isWindows)
        {
            return WindowsNames.TryGetValue(name, out var windowsName) ? windowsName : name;
        }

        var unixName = WindowsNames.FirstOrDefault(pair => pair.Value == name).Key;
        return unixName ?? name;
    }

    // Well-known tools and the name each is launched by on Windows (npm-style .cmd shims or
    // the .exe). On Linux/macOS the mapping is reversed.
    private static readonly IReadOnlyDictionary<string, string> WindowsNames = new Dictionary<string, string>
    {
        ["npm"] = "npm.cmd",
        ["npx"] = "npx.cmd",
        ["func"] = "func.cmd",
        ["yarn"] = "yarn.cmd",
        ["pnpm"] = "pnpm.cmd",
        ["node"] = "node.exe",
        ["dotnet"] = "dotnet.exe",
        ["docker"] = "docker.exe",
    };

    /// <summary>
    /// The file to hand to <c>ProcessStartInfo.FileName</c>. Bare names are looked up on
    /// <paramref name="pathValue"/>; a relative path such as <c>./run.sh</c> or
    /// <c>bin\dev.cmd</c> is resolved against <paramref name="workingDirectory"/> (the
    /// service's directory), not against DevDeck's own current directory, which is where
    /// Process.Start would otherwise look for it.
    /// </summary>
    public string ResolveForLaunch(string command, string? pathValue = null, string? workingDirectory = null)
    {
        var resolved = Resolve(command);
        if (string.IsNullOrWhiteSpace(resolved) || Path.IsPathRooted(resolved))
        {
            return resolved;
        }

        if (resolved.IndexOfAny(DirectorySeparators) >= 0)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                return resolved;
            }

            try
            {
                return Path.GetFullPath(resolved, workingDirectory);
            }
            catch (ArgumentException)
            {
                return resolved;
            }
        }

        var found = FindOnPath(resolved, pathValue);
        if (found is null && _isWindows && IsWindowsShimName(resolved))
        {
            // The well-known tools map to their npm-style shim (func -> func.cmd), but some are
            // installed as a plain executable instead — Azure Functions Core Tools from the MSI,
            // winget or Chocolatey puts only func.exe on PATH. Fall back to the usual PATHEXT
            // search for the bare name rather than failing on a shim that isn't there.
            found = FindOnPath(Path.GetFileNameWithoutExtension(resolved), pathValue);
        }

        return found ?? resolved;
    }

    // "func.cmd", "npm.cmd", "dotnet.exe", ... — the Windows names Resolve maps tools to.
    private static bool IsWindowsShimName(string name) =>
        WindowsNames.Values.Contains(name, StringComparer.OrdinalIgnoreCase);

    private string? FindOnPath(string command, string? pathValue)
    {
        var path = string.IsNullOrWhiteSpace(pathValue)
            ? Environment.GetEnvironmentVariable("PATH")
            : pathValue;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(_isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleanDirectory = directory.Trim('"');
            foreach (var candidateName in CandidateNames(command))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(cleanDirectory, candidateName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateNames(string command)
    {
        if (!_isWindows || Path.HasExtension(command))
        {
            yield return command;
            yield break;
        }

        // Prefer Windows-launchable PATHEXT shims before extensionless npm shims
        // such as "azurite", which are not valid ProcessStartInfo targets.
        foreach (var extension in WindowsPathExtensions())
        {
            yield return command + extension;
        }

        yield return command;
    }

    private static IEnumerable<string> WindowsPathExtensions()
    {
        var value = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = ".COM;.EXE;.BAT;.CMD";
        }

        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.ToLowerInvariant());
    }
}
