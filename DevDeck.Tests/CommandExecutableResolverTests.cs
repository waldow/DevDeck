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
}
