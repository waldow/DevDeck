using System.Collections.Concurrent;

namespace DevDeck.Web.Services.Health;

public sealed class HealthStatusCache
{
    private readonly ConcurrentDictionary<int, string> _statuses = new();

    public void Set(int serviceId, string status) => _statuses[serviceId] = status;

    public string Get(int serviceId) =>
        _statuses.TryGetValue(serviceId, out var status) ? status : HealthStatusNames.Unknown;

    public bool IsHealthy(int serviceId) =>
        string.Equals(Get(serviceId), HealthStatusNames.Healthy, StringComparison.OrdinalIgnoreCase);
}
