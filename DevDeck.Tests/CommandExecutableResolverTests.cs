using DevDeck.Web.Services.Commands;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class CommandExecutableResolverTests
{
    [Theory]
    [InlineData("npm", "npm.cmd")]
    [InlineData("npx", "npx.cmd")]
    [InlineData("func", "func.cmd")]
    [InlineData("node", "node.exe")]
    [InlineData("dotnet", "dotnet.exe")]
    [InlineData("docker", "docker.exe")]
    [InlineData("npm.cmd", "npm.cmd")]
    [InlineData("custom-tool", "custom-tool")]
    public void Windows_adds_extension(string input, string expected)
    {
        var r = new CommandExecutableResolver(isWindows: true);
        r.Resolve(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("npm.cmd", "npm")]
    [InlineData("npx.cmd", "npx")]
    [InlineData("func.cmd", "func")]
    [InlineData("node.exe", "node")]
    [InlineData("dotnet.exe", "dotnet")]
    [InlineData("npm", "npm")]
    [InlineData("custom-tool", "custom-tool")]
    public void Linux_strips_extension(string input, string expected)
    {
        var r = new CommandExecutableResolver(isWindows: false);
        r.Resolve(input).Should().Be(expected);
    }

    [Fact]
    public void Absolute_paths_pass_through_unchanged()
    {
        var rWin = new CommandExecutableResolver(isWindows: true);
        var rNix = new CommandExecutableResolver(isWindows: false);
        rWin.Resolve("/usr/bin/node").Should().Be("/usr/bin/node");
        rNix.Resolve("/usr/bin/node").Should().Be("/usr/bin/node");
    }

    [Fact]
    public void ResolveForLaunch_resolves_simple_windows_command_from_path()
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-resolver-");
        try
        {
            var npm = Path.Combine(temp.FullName, "npm.cmd");
            File.WriteAllText(npm, "@echo off");

            var r = new CommandExecutableResolver(isWindows: true);

            r.ResolveForLaunch("npm", temp.FullName).Should().Be(Path.GetFullPath(npm));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveForLaunch_prefers_windows_path_extension_over_extensionless_shim()
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-resolver-");
        try
        {
            var shim = Path.Combine(temp.FullName, "azurite");
            var cmd = Path.Combine(temp.FullName, "azurite.cmd");
            File.WriteAllText(shim, "#!/bin/sh");
            File.WriteAllText(cmd, "@echo off");

            var r = new CommandExecutableResolver(isWindows: true);

            r.ResolveForLaunch("azurite", temp.FullName).Should().Be(Path.GetFullPath(cmd));
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveForLaunch_does_not_path_search_explicit_relative_path()
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-resolver-");
        try
        {
            File.WriteAllText(Path.Combine(temp.FullName, "npm.cmd"), "@echo off");

            var r = new CommandExecutableResolver(isWindows: true);

            r.ResolveForLaunch(".\\npm.cmd", temp.FullName).Should().Be(".\\npm.cmd");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveForLaunch_leaves_command_when_path_has_no_match()
    {
        var r = new CommandExecutableResolver(isWindows: true);

        r.ResolveForLaunch("custom-tool", "C:\\definitely-not-a-real-devdeck-path").Should().Be("custom-tool");
    }
}
