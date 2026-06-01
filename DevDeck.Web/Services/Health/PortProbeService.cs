using System.Net.Sockets;
using DevDeck.Web.Services.Runtime;

namespace DevDeck.Web.Services.Health;

public sealed class PortProbeService
{
    private readonly IDevDeckProcessManager _processManager;

    public PortProbeService(IDevDeckProcessManager processManager)
    {
        _processManager = processManager;
    }

    public async Task<PortStatus> ProbeAsync(int port, int? owningServiceId = null, CancellationToken cancellationToken = default)
    {
        var open = await IsPortOpenAsync(port, cancellationToken);
        if (!open)
        {
            return PortStatus.Free;
        }

        if (owningServiceId is int ownerId && _processManager.GetRunningProcess(ownerId)?.Port == port)
        {
            return PortStatus.UsedByDevDeckService;
        }

        foreach (var running in _processManager.GetRunningProcesses())
        {
            if (running.Port == port)
            {
                return PortStatus.UsedByDevDeckService;
            }
        }

        return PortStatus.UsedByOtherProcess;
    }

    public async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken = default)
        => await IsEndpointOpenAsync("127.0.0.1", port, cancellationToken);

    public async Task<bool> IsEndpointOpenAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        foreach (var candidate in ProbeHosts(host))
        {
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(candidate, port, cts.Token);
                if (client.Connected)
                {
                    return true;
                }
            }
            catch
            {
                // Try the next loopback alias or report closed below.
            }
        }

        return false;
    }

    private static IEnumerable<string> ProbeHosts(string host)
    {
        if (IsLocalhost(host))
        {
            yield return "127.0.0.1";
            yield return "::1";
        }

        yield return host;
    }

    private static bool IsLocalhost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
