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

        public void ClearLiveLogs(int serviceId)
        {
        }

        public void AppendProxyLog(RunningProcessInfo info, string text)
        {
        }
    }
}
