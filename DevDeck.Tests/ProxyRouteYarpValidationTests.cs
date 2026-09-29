using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace DevDeck.Tests;

// YARP rejects a whole config snapshot when any one route in it is invalid, which would freeze
// every later route edit and make the initial load throw at startup. The builder must run YARP's
// own validator per route and drop only the offenders.
public sealed class ProxyRouteYarpValidationTests
{
    private readonly IConfigValidator _yarpValidator;
    private readonly ProxyRouteBuilder _builder;

    public ProxyRouteYarpValidationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddReverseProxy();
        _yarpValidator = services.BuildServiceProvider().GetRequiredService<IConfigValidator>();
        _builder = new ProxyRouteBuilder(new ProxyDestinationValidator(allowExternal: false), yarpValidator: _yarpValidator);
    }

    [Fact]
    public async Task BuildAsync_drops_a_route_with_an_unknown_authorization_policy_and_keeps_the_rest()
    {
        var result = await _builder.BuildAsync(
        [
            Route(1, "Good"),
            Route(2, "Locked", r => r.AuthorizationPolicy = "no-such-policy"),
        ]);

        result.Routes.Should().ContainSingle().Which.RouteId.Should().Be("route-1");
        result.Clusters.Should().ContainSingle().Which.ClusterId.Should().Be("cluster-1");
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("[Locked]").And.Contain("no-such-policy").And.NotContain("route-2");
        await AssertYarpAcceptsAsync(result);
    }

    [Fact]
    public async Task BuildAsync_drops_a_route_with_a_host_yarp_rejects()
    {
        var result = await _builder.BuildAsync(
        [
            Route(1, "Good"),
            Route(2, "Punycode", r => r.MatchHostsCsv = "app.localhost, xn--bcher-kva.localhost"),
        ]);

        result.Routes.Should().ContainSingle().Which.RouteId.Should().Be("route-1");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("[Punycode]");
        await AssertYarpAcceptsAsync(result);
    }

    [Fact]
    public async Task BuildAsync_accepts_the_builtin_authorization_policies()
    {
        var result = await _builder.BuildAsync(
        [
            Route(1, "Default", r => r.AuthorizationPolicy = "Default"),
            Route(2, "Anonymous", r => r.AuthorizationPolicy = "Anonymous"),
        ]);

        result.Routes.Should().HaveCount(2);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateAsync_reports_yarp_errors_for_a_single_candidate_by_name()
    {
        var errors = await _builder.ValidateAsync(Route(0, "Candidate", r => r.AuthorizationPolicy = "admins"));

        errors.Should().ContainSingle().Which.Should().Contain("admins").And.Contain("'Candidate'");
    }

    [Fact]
    public async Task ValidateAsync_checks_disabled_routes_too()
    {
        var errors = await _builder.ValidateAsync(Route(0, "Off", r =>
        {
            r.Enabled = false;
            r.AuthorizationPolicy = "admins";
        }));

        errors.Should().ContainSingle();
    }

    [Fact]
    public async Task ValidateAsync_reports_builder_rules_before_yarp()
    {
        var errors = await _builder.ValidateAsync(Route(0, "CatchAll", r => r.MatchPath = "/{**path}"));

        errors.Should().ContainSingle().Which.Should().Contain("Catch-all");
    }

    private async Task AssertYarpAcceptsAsync(ProxyBuildResult result)
    {
        foreach (var route in result.Routes)
        {
            (await _yarpValidator.ValidateRouteAsync(route)).Should().BeEmpty();
        }
        foreach (var cluster in result.Clusters)
        {
            (await _yarpValidator.ValidateClusterAsync(cluster)).Should().BeEmpty();
        }
    }

    private static ProxyRoute Route(int id, string name, Action<ProxyRoute>? configure = null)
    {
        var route = new ProxyRoute
        {
            Id = id,
            Name = name,
            Enabled = true,
            MatchPath = $"/r{id}/{{**catch-all}}",
            PathTransformMode = "None",
            DestinationUrlOverride = "http://localhost:5173/",
        };
        configure?.Invoke(route);
        return route;
    }
}
