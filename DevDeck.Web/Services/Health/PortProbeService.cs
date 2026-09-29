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

    /// <summary>
    /// Whether anything is listening on a local port — on 127.0.0.1 or ::1, like the
    /// "localhost" the proxy and health checks connect to. (A Node 17+ dev server bound to
    /// localhost often listens on ::1 only.)
    /// </summary>
    public Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken = default)
        => IsEndpointOpenAsync("localhost", port, cancellationToken);

    public async Task<bool> IsEndpointOpenAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        // Probe the candidates concurrently so a closed port costs one timeout, not one per
        // loopback alias (a refused loopback connect can take the full timeout on Windows).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(250));
        var probes = ProbeHosts(host).Select(candidate => TryConnectAsync(candidate, port, cts.Token)).ToList();
        while (probes.Count > 0)
        {
            var done = await Task.WhenAny(probes);
            if (await done)
            {
                cts.Cancel(); // release the other attempts now rather than at their timeout
                return true;
            }
            probes.Remove(done);
        }

        return false;
    }

    private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ProbeHosts(string host)
    {
        if (IsLocalhost(host))
        {
            yield return "127.0.0.1";
            yield return "::1";
            // Plain "localhost" is exactly those two; a *.localhost name may resolve elsewhere.
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }
        }

        yield return host;
    }

    private static bool IsLocalhost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
