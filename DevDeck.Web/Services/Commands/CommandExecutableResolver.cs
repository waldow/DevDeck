using System.Runtime.InteropServices;

namespace DevDeck.Web.Services.Commands;

public sealed class CommandExecutableResolver
{
    private readonly bool _isWindows;

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
}
