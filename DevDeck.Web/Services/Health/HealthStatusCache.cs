using System.Collections.Concurrent;

namespace DevDeck.Web.Services.Health;

/// <summary>
/// In-memory health state: the latest result of every polled check, the post-start warm-up
/// windows, and which services have any enabled check at all. The proxy health gate and the UI
/// pills both read it, so they always agree.
/// </summary>
public sealed class HealthStatusCache
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, CheckResult>> _byService = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _warmupUntil = new();

    // Services with at least one enabled check, as of the poller's last pass; null until the
    // first pass, when it isn't known yet.
    private volatile IReadOnlySet<int>? _monitoredServices;

    public void Set(int serviceId, int checkId, string status, DateTimeOffset? checkedUtc = null)
    {
        var checks = _byService.GetOrAdd(serviceId, _ => new ConcurrentDictionary<int, CheckResult>());
        checks[checkId] = new CheckResult(status, checkedUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>The last recorded result of one check, if it has run since the service last stopped.</summary>
    public bool TryGetResult(int serviceId, int checkId, out CheckResult result)
    {
        if (_byService.TryGetValue(serviceId, out var checks) && checks.TryGetValue(checkId, out var found))
        {
            result = found;
            return true;
        }

        result = default;
        return false;
    }

    // Records that a service has just been started so health gates pass until the
    // background poller has had a chance to record real check results.
    public void MarkStarting(int serviceId, TimeSpan warmupWindow)
    {
        _warmupUntil[serviceId] = DateTimeOffset.UtcNow + warmupWindow;
    }

    /// <summary>
    /// Drops cached results for checks that are no longer polled (disabled or deleted), so a
    /// stale Unhealthy can't keep failing a RequireHealthyDestination gate. Warm-up windows are
    /// kept.
    /// </summary>
    public void RetainChecks(IReadOnlySet<int> polledCheckIds)
    {
        foreach (var (serviceId, checks) in _byService)
        {
            foreach (var checkId in checks.Keys)
            {
                if (!polledCheckIds.Contains(checkId))
                {
                    checks.TryRemove(checkId, out _);
                }
            }
            if (checks.IsEmpty)
            {
                _byService.TryRemove(new KeyValuePair<int, ConcurrentDictionary<int, CheckResult>>(serviceId, checks));
            }
        }
    }

    /// <summary>Records which services have at least one enabled health check.</summary>
    public void SetMonitoredServices(IReadOnlySet<int> serviceIds) => _monitoredServices = serviceIds;

    /// <summary>
    /// True when the service is known to have no enabled health check, so there is nothing to
    /// gate on (false before the poller's first pass).
    /// </summary>
    public bool HasNoChecks(int serviceId) =>
        _monitoredServices is { } monitored && !monitored.Contains(serviceId);

    public void RemoveService(int serviceId)
    {
        _byService.TryRemove(serviceId, out _);
        _warmupUntil.TryRemove(serviceId, out _);
    }

    public string Get(int serviceId)
    {
        if (!_byService.TryGetValue(serviceId, out var checks) || checks.IsEmpty)
        {
            return IsInWarmup(serviceId) ? HealthStatusNames.Warming : HealthStatusNames.Unknown;
        }

        var statuses = checks.Values.Select(c => c.Status).ToArray();
        if (statuses.Any(s => string.Equals(s, HealthStatusNames.Unhealthy, StringComparison.OrdinalIgnoreCase)))
            return HealthStatusNames.Unhealthy;
        if (statuses.Any(s => string.Equals(s, HealthStatusNames.Timeout, StringComparison.OrdinalIgnoreCase)))
            return HealthStatusNames.Timeout;
        if (statuses.Any(s => string.Equals(s, HealthStatusNames.NotRunning, StringComparison.OrdinalIgnoreCase)))
            return HealthStatusNames.NotRunning;
        if (statuses.All(s => string.Equals(s, HealthStatusNames.Healthy, StringComparison.OrdinalIgnoreCase)))
            return HealthStatusNames.Healthy;
        return HealthStatusNames.Unknown;
    }

    /// <summary>
    /// The RequireHealthyDestination gate: passes during the post-start warm-up, for a service
    /// with no enabled health check (nothing could ever make it Healthy), and otherwise only
    /// when every check's latest result is Healthy.
    /// </summary>
    public bool IsHealthy(int serviceId)
    {
        if (IsInWarmup(serviceId) || HasNoChecks(serviceId)) return true;
        return string.Equals(Get(serviceId), HealthStatusNames.Healthy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The health pill for a service: its aggregate status while it is up, else NotRunning.</summary>
    public string GetDisplayStatus(int serviceId, bool isUp) =>
        isUp ? Get(serviceId) : HealthStatusNames.NotRunning;

    private bool IsInWarmup(int serviceId) =>
        _warmupUntil.TryGetValue(serviceId, out var until) && DateTimeOffset.UtcNow < until;

    public readonly record struct CheckResult(string Status, DateTimeOffset CheckedUtc);
}
