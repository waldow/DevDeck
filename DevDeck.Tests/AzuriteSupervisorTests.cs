using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDeck.Tests;

public sealed class AzuriteSupervisorTests
{
    [Fact]
    public async Task EnsureRunning_treats_already_listening_ports_as_healthy_without_launching()
    {
        // Bind the three configured ports so the supervisor sees Azurite as already up.
        using var blob = Listen();
        using var queue = Listen();
        using var table = Listen();

        var options = new DevDeckOptions
        {
            Azurite = new AzuriteOptions
            {
                BlobPort = Port(blob),
                QueuePort = Port(queue),
                TablePort = Port(table),
            },
        };
        var supervisor = CreateSupervisor(options);
        var messages = new List<string>();

        var result = await supervisor.EnsureRunningAsync(messages.Add, CancellationToken.None);

        result.Success.Should().BeTrue();
        messages.Should().ContainSingle(m => m.Contains("already running"));
    }

    [Fact]
    public async Task EnsureRunning_returns_helpful_error_when_executable_is_missing()
    {
        var options = new DevDeckOptions
        {
            Azurite = new AzuriteOptions
            {
                // A command that is not on PATH so the launch attempt fails fast.
                Command = "devdeck-nonexistent-azurite-" + Guid.NewGuid().ToString("N"),
                BlobPort = FreePort(),
                QueuePort = FreePort(),
                TablePort = FreePort(),
                StartupTimeoutSeconds = 1,
            },
        };
        var supervisor = CreateSupervisor(options);

        var result = await supervisor.EnsureRunningAsync(_ => { }, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("npm install -g azurite");
    }

    [Theory]
    [InlineData(false, true)]  // default: Functions hosts outlive DevDeck, so Azurite must too
    [InlineData(true, false)]  // StopServicesOnShutdown: stopped along with the services
    public async Task DisposeAsync_stops_a_launched_azurite_only_when_services_are_stopped_on_shutdown(
        bool stopServicesOnShutdown, bool expectAlive)
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-azurite-");
        int? pid = null;
        try
        {
            var options = new DevDeckOptions
            {
                StopServicesOnShutdown = stopServicesOnShutdown,
                Azurite = new AzuriteOptions
                {
                    Command = WriteFakeAzurite(temp.FullName),
                    BlobPort = FreePort(),
                    QueuePort = FreePort(),
                    TablePort = FreePort(),
                    StartupTimeoutSeconds = 1,
                },
            };
            var supervisor = CreateSupervisor(options);
            var messages = new List<string>();

            // The fake never opens the ports, so this times out — but leaves the process tracked.
            await supervisor.EnsureRunningAsync(messages.Add, CancellationToken.None);
            var launched = messages.Select(m => Regex.Match(m, @"Launched Azurite \(PID (\d+)\)")).Single(m => m.Success);
            pid = int.Parse(launched.Groups[1].Value);

            await supervisor.DisposeAsync();

            IsAlive(pid.Value).Should().Be(expectAlive);
        }
        finally
        {
            if (pid is int leftover && IsAlive(leftover))
            {
                using var process = Process.GetProcessById(leftover);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            temp.Delete(recursive: true);
        }
    }

    // A stand-in "azurite" that ignores its arguments and just stays alive.
    private static string WriteFakeAzurite(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(folder, "fake-azurite.cmd");
            File.WriteAllText(cmd, "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n");
            return cmd;
        }

        var script = Path.Combine(folder, "fake-azurite");
        File.WriteAllText(script, "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static AzuriteSupervisor CreateSupervisor(DevDeckOptions options) =>
        new(
            new CommandExecutableResolver(),
            new LogFileWriter(),
            new TestOptionsMonitor<DevDeckOptions>(options),
            NullLogger<AzuriteSupervisor>.Instance);

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
