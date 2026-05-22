using System.Net;
using System.Net.Sockets;
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
