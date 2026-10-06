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

    // When each service's results were last dropped: a check that was already in flight then
    // (against the old URL, or the process that just stopped) must not record its result.
    private readonly ConcurrentDictionary<int, DateTimeOffset> _resetUtc = new();

    // Passthru services granted their one warm-up since their instance was last seen down.
    private readonly ConcurrentDictionary<int, byte> _externalWarmupGranted = new();

    // Services with at least one enabled check, as of the poller's last pass; null until the
    // first pass, when it isn't known yet.
    private volatile IReadOnlySet<int>? _monitoredServices;

    public void Set(int serviceId, int checkId, string status, DateTimeOffset? checkedUtc = null)
    {
        var at = checkedUtc ?? DateTimeOffset.UtcNow;
        if (_resetUtc.TryGetValue(serviceId, out var reset) && at < reset)
        {
            return;
        }

        var checks = _byService.GetOrAdd(serviceId, _ => new ConcurrentDictionary<int, CheckResult>());
        checks[checkId] = new CheckResult(status, at);
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
    /// A passthru instance is up but not yet known healthy: grants it a warm-up — once, until
    /// <see cref="ExternalInstanceDown"/>. Re-granting on every request would keep the gate open
    /// for good when a check stays NotRunning (one that probes another host or port).
    /// </summary>
    public void MarkExternalStarting(int serviceId, TimeSpan warmupWindow)
    {
        if (_externalWarmupGranted.TryAdd(serviceId, 0))
        {
            MarkStarting(serviceId, warmupWindow);
        }
    }

    /// <summary>The passthru instance was seen down: its next start may warm up again.</summary>
    public void ExternalInstanceDown(int serviceId) => _externalWarmupGranted.TryRemove(serviceId, out _);

    /// <summary>
    /// True when a result was recorded during the service's latest warm-up window. A failure
    /// then (connection refused while the service was still coming up) says nothing about it
    /// once up, so the poller re-runs such a check on its next pass instead of a full interval later.
    /// </summary>
    public bool RecordedDuringWarmup(int serviceId, DateTimeOffset checkedUtc) =>
        _warmupUntil.TryGetValue(serviceId, out var until) && checkedUtc < until;

    /// <summary>
    /// Drops a service's check results (keeping its warm-up), e.g. after its checks or port were
    /// edited, so the checks run again on the next pass rather than a stale result standing for
    /// a whole interval.
    /// </summary>
    public void ForgetResults(int serviceId)
    {
        _resetUtc[serviceId] = DateTimeOffset.UtcNow;
        _byService.TryRemove(serviceId, out _);
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
        _resetUtc[serviceId] = DateTimeOffset.UtcNow;
        _byService.TryRemove(serviceId, out _);
        _warmupUntil.TryRemove(serviceId, out _);
        _externalWarmupGranted.TryRemove(serviceId, out _);
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
