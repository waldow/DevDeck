using DevDeck.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace DevDeck.Web.Services.Proxy;

public sealed class DevDeckProxyConfigProvider : IProxyConfigProvider
{
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProxyRouteBuilder _builder;
    private readonly ILogger<DevDeckProxyConfigProvider> _logger;
    private volatile Snapshot _snapshot;

    // Reloads read the database and then publish; serialized so a reload that read older rows
    // can never publish after (and so overwrite) one that read newer rows.
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public DevDeckProxyConfigProvider(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProxyRouteBuilder builder,
        ILogger<DevDeckProxyConfigProvider> logger)
    {
        _dbFactory = dbFactory;
        _builder = builder;
        _logger = logger;
        _snapshot = new Snapshot(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>(), Array.Empty<string>());
    }

    public IProxyConfig GetConfig() => _snapshot;

    public IReadOnlyList<string> LastWarnings => _snapshot.Warnings;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(CancellationToken.None);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var routes = await db.ProxyRoutes
                .Include(r => r.DevService)
                .Where(r => r.Enabled)
                .OrderBy(r => r.Order)
                .ToListAsync(cancellationToken);

            var build = await _builder.BuildAsync(routes);
            var old = Interlocked.Exchange(ref _snapshot,
                new Snapshot(build.Routes, build.Clusters, build.Warnings));
            old.SignalChange();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload proxy config");
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private sealed class Snapshot : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public Snapshot(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters, IReadOnlyList<string> warnings)
        {
            Routes = routes;
            Clusters = clusters;
            Warnings = warnings;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IReadOnlyList<string> Warnings { get; }
        public IChangeToken ChangeToken { get; }

        public void SignalChange()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* already signaled */ }
        }
    }
}
