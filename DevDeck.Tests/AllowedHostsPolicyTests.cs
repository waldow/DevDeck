using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;

namespace DevDeck.Tests;

// DevDeck has no authentication, so it must not answer requests addressed to arbitrary host
// names (DNS rebinding). AllowedHosts is the allow-list.
public sealed class AllowedHostsPolicyTests
{
    [Fact]
    public void Loopback_names_and_a_concrete_gateway_host_are_always_allowed()
    {
        var options = new HostFilteringOptions { AllowedHosts = new[] { "devbox.lan" } };

        AllowedHostsPolicy.IncludeDevDeckHosts(options, "http://gateway.lan:5050");

        options.AllowedHosts.Should().BeEquivalentTo("devbox.lan", "localhost", "*.localhost", "127.0.0.1", "[::1]", "gateway.lan");
    }

    [Theory]
    [InlineData("http://0.0.0.0:5050")]
    [InlineData("http://[::]:5050")]
    public void A_bind_to_every_interface_adds_no_host(string listenUrl)
    {
        var options = new HostFilteringOptions { AllowedHosts = new[] { "localhost" } };

        AllowedHostsPolicy.IncludeDevDeckHosts(options, listenUrl);

        options.AllowedHosts.Should().BeEquivalentTo("localhost", "*.localhost", "127.0.0.1", "[::1]");
    }

    [Fact]
    public void An_allow_everything_list_is_left_alone()
    {
        var options = new HostFilteringOptions { AllowedHosts = new[] { "*" } };

        AllowedHostsPolicy.IncludeDevDeckHosts(options, "http://localhost:5050");

        options.AllowedHosts.Should().Equal("*");
    }

    [Fact]
    public void Route_hosts_outside_the_list_are_reported()
    {
        var policy = Policy("localhost", "*.localhost", "127.0.0.1", "[::1]");

        policy.Blocked(["app.localhost", "app.localhost:5050", "*.localhost", "localhost:*", "api.mydev.test", "*.mydev.test"])
            .Should().Equal("api.mydev.test", "*.mydev.test");
    }

    [Fact]
    public void Nothing_is_reported_when_every_host_is_allowed()
    {
        Policy("*").Blocked(["api.mydev.test"]).Should().BeEmpty();
    }

    private static AllowedHostsPolicy Policy(params string[] allowed) =>
        new(new StaticMonitor(new HostFilteringOptions { AllowedHosts = allowed.ToList() }));

    private sealed class StaticMonitor(HostFilteringOptions value) : IOptionsMonitor<HostFilteringOptions>
    {
        public HostFilteringOptions CurrentValue => value;
        public HostFilteringOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<HostFilteringOptions, string?> listener) => null;
    }
}
