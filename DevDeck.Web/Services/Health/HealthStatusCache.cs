using System.Collections.Concurrent;

namespace DevDeck.Web.Services.Health;

public sealed class HealthStatusCache
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, string>> _byService = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _warmupUntil = new();

    public void Set(int serviceId, int checkId, string status)
    {
        var checks = _byService.GetOrAdd(serviceId, _ => new ConcurrentDictionary<int, string>());
        checks[checkId] = status;
    }

    // Records that a service has just been started so health gates pass until the
    // background poller has had a chance to record real check results.
    public void MarkStarting(int serviceId, TimeSpan warmupWindow)
    {
        _warmupUntil[serviceId] = DateTimeOffset.UtcNow + warmupWindow;
    }

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

        var statuses = checks.Values.ToArray();
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

    public bool IsHealthy(int serviceId)
    {
        if (IsInWarmup(serviceId)) return true;
        return string.Equals(Get(serviceId), HealthStatusNames.Healthy, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInWarmup(int serviceId) =>
        _warmupUntil.TryGetValue(serviceId, out var until) && DateTimeOffset.UtcNow < until;
}
