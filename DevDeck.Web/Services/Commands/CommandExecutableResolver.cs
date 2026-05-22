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
            return name switch
            {
                "npm" => "npm.cmd",
                "npx" => "npx.cmd",
                "func" => "func.cmd",
                "yarn" => "yarn.cmd",
                "pnpm" => "pnpm.cmd",
                "node" => "node.exe",
                "dotnet" => "dotnet.exe",
                "docker" => "docker.exe",
                _ => name,
            };
        }

        return name switch
        {
            "npm.cmd" => "npm",
            "npx.cmd" => "npx",
            "func.cmd" => "func",
            "yarn.cmd" => "yarn",
            "pnpm.cmd" => "pnpm",
            "node.exe" => "node",
            "dotnet.exe" => "dotnet",
            "docker.exe" => "docker",
            _ => name,
        };
    }

    public string ResolveForLaunch(string command, string? pathValue = null)
    {
        var resolved = Resolve(command);
        if (string.IsNullOrWhiteSpace(resolved) ||
            Path.IsPathRooted(resolved) ||
            resolved.IndexOfAny(DirectorySeparators) >= 0)
        {
            return resolved;
        }

        return FindOnPath(resolved, pathValue) ?? resolved;
    }

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
