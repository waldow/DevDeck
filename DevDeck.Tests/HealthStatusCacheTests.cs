using DevDeck.Web.Services.Health;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class HealthStatusCacheTests
{
    [Fact]
    public void Unknown_service_returns_Unknown_and_is_not_healthy()
    {
        var cache = new HealthStatusCache();
        cache.Get(123).Should().Be(HealthStatusNames.Unknown);
        cache.IsHealthy(123).Should().BeFalse();
    }

    [Fact]
    public void Single_healthy_check_makes_service_healthy()
    {
        var cache = new HealthStatusCache();
        cache.Set(serviceId: 1, checkId: 10, HealthStatusNames.Healthy);
        cache.Get(1).Should().Be(HealthStatusNames.Healthy);
        cache.IsHealthy(1).Should().BeTrue();
    }

    [Fact]
    public void Multiple_checks_one_unhealthy_makes_service_unhealthy()
    {
        var cache = new HealthStatusCache();
        cache.Set(1, 10, HealthStatusNames.Healthy);
        cache.Set(1, 11, HealthStatusNames.Unhealthy);
        cache.Get(1).Should().Be(HealthStatusNames.Unhealthy);
        cache.IsHealthy(1).Should().BeFalse();
    }

    [Fact]
    public void Multiple_checks_aggregation_does_not_clobber_between_checks()
    {
        // Regression: prior cache wrote service-level status on each per-check call so
        // two checks for one service would clobber each other depending on poll order.
        var cache = new HealthStatusCache();
        cache.Set(1, 10, HealthStatusNames.Healthy);
        cache.Set(1, 11, HealthStatusNames.Unhealthy);
        cache.Set(1, 10, HealthStatusNames.Healthy); // re-confirm healthy on check 10
        cache.Get(1).Should().Be(HealthStatusNames.Unhealthy);
    }

    [Fact]
    public void Timeout_outranks_NotRunning_in_aggregation()
    {
        var cache = new HealthStatusCache();
        cache.Set(1, 10, HealthStatusNames.NotRunning);
        cache.Set(1, 11, HealthStatusNames.Timeout);
        cache.Get(1).Should().Be(HealthStatusNames.Timeout);
    }

    [Fact]
    public void Warmup_window_makes_service_appear_healthy_before_any_check_runs()
    {
        var cache = new HealthStatusCache();
        cache.MarkStarting(1, TimeSpan.FromMinutes(1));
        cache.IsHealthy(1).Should().BeTrue();
        cache.Get(1).Should().Be(HealthStatusNames.Warming);
    }

    [Fact]
    public void Warmup_does_not_mask_a_failing_check_once_recorded()
    {
        var cache = new HealthStatusCache();
        cache.MarkStarting(1, TimeSpan.FromMinutes(1));
        cache.Set(1, 10, HealthStatusNames.Unhealthy);
        // Aggregation surfaces the unhealthy check…
        cache.Get(1).Should().Be(HealthStatusNames.Unhealthy);
        // …but IsHealthy still returns true during warmup so the proxy gate doesn't
        // 503 a freshly-started service while the first check result is still settling.
        cache.IsHealthy(1).Should().BeTrue();
    }

    [Fact]
    public void Expired_warmup_no_longer_masks_status()
    {
        var cache = new HealthStatusCache();
        cache.MarkStarting(1, TimeSpan.Zero);
        cache.IsHealthy(1).Should().BeFalse();
        cache.Get(1).Should().Be(HealthStatusNames.Unknown);
    }

    [Fact]
    public void RemoveService_clears_per_check_entries_and_warmup()
    {
        var cache = new HealthStatusCache();
        cache.Set(1, 10, HealthStatusNames.Healthy);
        cache.MarkStarting(1, TimeSpan.FromMinutes(1));
        cache.RemoveService(1);
        cache.Get(1).Should().Be(HealthStatusNames.Unknown);
        cache.IsHealthy(1).Should().BeFalse();
    }

    [Fact]
    public void Different_services_are_isolated()
    {
        var cache = new HealthStatusCache();
        cache.Set(1, 10, HealthStatusNames.Healthy);
        cache.Set(2, 10, HealthStatusNames.Unhealthy);
        cache.IsHealthy(1).Should().BeTrue();
        cache.IsHealthy(2).Should().BeFalse();
    }
}
