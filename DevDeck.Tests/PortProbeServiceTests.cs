using System.Net;
using System.Net.Sockets;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class PortProbeServiceTests
{
    [Fact]
    public async Task IsEndpointOpenAsync_connects_to_the_supplied_host_and_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new PortProbeService(new FakeProcessManager());

            var open = await probe.IsEndpointOpenAsync("127.0.0.1", port);

            open.Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsEndpointOpenAsync_treats_localhost_subdomains_as_loopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new PortProbeService(new FakeProcessManager());

            var open = await probe.IsEndpointOpenAsync("api.localhost", port);

            open.Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsPortOpenAsync_sees_a_server_listening_only_on_ipv6_loopback()
    {
        // e.g. a Node 17+ dev server bound to "localhost" that resolved to ::1.
        if (!Socket.OSSupportsIPv6) return;
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new PortProbeService(new FakeProcessManager());

            (await probe.IsPortOpenAsync(port)).Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task IsPortOpenAsync_reports_a_closed_port_within_one_probe_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var probe = new PortProbeService(new FakeProcessManager());
        // Untimed first probe: JIT and thread-pool start-up (the whole suite starts at once)
        // must not count against the probe's own timing.
        await probe.IsPortOpenAsync(port);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var open = await probe.IsPortOpenAsync(port);

        open.Should().BeFalse();
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(450), "the loopback aliases are probed concurrently");
    }

    private sealed class FakeProcessManager : IDevDeckProcessManager
    {
        public Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StopServiceResult { ServiceId = serviceId, Success = false });

        public Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new RestartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartProfileResult { ProfileId = profileId, Success = false });

        public Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartAllResult());

        public Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StopAllResult());

        public RunningProcessInfo? GetRunningProcess(int serviceId) => null;

        public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => [];

        public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => [];

        public LiveLogSlice GetLiveLogsSince(int serviceId, long since) => new([], 0, 0, false);

        public bool IsServiceBusy(int serviceId) => false;

        public bool IsServiceStarting(int serviceId) => false;

        public void ClearLiveLogs(int serviceId)
        {
        }

        public void AppendProxyLog(RunningProcessInfo info, string text)
        {
        }
    }
}
