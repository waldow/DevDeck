using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DevDeck.Tests;

public sealed class GatewayUrlResolverTests
{
    [Fact]
    public void ResolveListenUrl_uses_configured_gateway_base_url()
    {
        var configuration = BuildConfiguration("http://localhost:6420");

        GatewayUrlResolver.ResolveListenUrl(configuration).Should().Be("http://localhost:6420");
    }

    [Fact]
    public void ResolveListenUrl_ignores_path_when_binding_kestrel()
    {
        var configuration = BuildConfiguration("http://localhost:6420/app");

        GatewayUrlResolver.ResolveListenUrl(configuration).Should().Be("http://localhost:6420");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost:6420")]
    public void ResolveListenUrl_falls_back_to_default_for_invalid_values(string? gatewayBaseUrl)
    {
        var configuration = BuildConfiguration(gatewayBaseUrl);

        GatewayUrlResolver.ResolveListenUrl(configuration).Should().Be(GatewayUrlResolver.DefaultGatewayBaseUrl);
    }

    [Theory]
    [InlineData("http://localhost:5050")]
    [InlineData("http://LOCALHOST:5050")]
    [InlineData("http://127.0.0.1:5050")]
    [InlineData("http://[::1]:5050")]
    [InlineData("http://app.localhost:5050")]
    public void IsLoopbackHost_accepts_local_binds(string listenUrl)
    {
        GatewayUrlResolver.IsLoopbackHost(listenUrl).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5050")]
    [InlineData("http://192.168.1.5:5050")]
    [InlineData("http://10.0.0.4:5050")]
    [InlineData("http://mydevbox:5050")]
    [InlineData("http://example.com:5050")]
    [InlineData("not-a-url")]
    public void IsLoopbackHost_flags_network_reachable_binds(string listenUrl)
    {
        GatewayUrlResolver.IsLoopbackHost(listenUrl).Should().BeFalse();
    }

    private static IConfiguration BuildConfiguration(string? gatewayBaseUrl)
    {
        var values = new Dictionary<string, string?>();
        if (gatewayBaseUrl is not null)
        {
            values["DevDeck:ReverseProxy:GatewayBaseUrl"] = gatewayBaseUrl;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
