using System.Net;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace DevDeck.Tests;

// Routing builds ONE matcher over every endpoint — MVC's /Manage included. A route whose hosts
// or constraints it can't parse makes that build throw, so routing freezes at runtime and every
// request is a 500 after a restart; two routes it can't tell apart make every request they
// match a 500. The builder must keep all of these out of the snapshot, and the editor/importer
// must refuse them.
public sealed class ProxyRouteRoutingSafetyTests
{
    private readonly ProxyRouteBuilder _builder;

    public ProxyRouteRoutingSafetyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddAuthorization();
        services.AddReverseProxy();
        var provider = services.BuildServiceProvider();
        var options = new DevDeckOptions();
        options.ReverseProxy.GatewayBaseUrl = "http://localhost:5050";
        _builder = new ProxyRouteBuilder(
            new ProxyDestinationValidator(allowExternal: false),
            options: new StaticOptionsMonitor(options),
            yarpValidator: provider.GetRequiredService<IConfigValidator>(),
            parameterPolicyFactory: provider.GetRequiredService<ParameterPolicyFactory>());
    }

    [Theory]
    [InlineData("http://localhost:5050")]
    [InlineData("[::1]")]
    [InlineData("[::1]:5050")]
    [InlineData("localhost:abc")]
    [InlineData("app.localhost/path")]
    public async Task Match_hosts_routing_cannot_parse_are_refused(string hosts)
    {
        var errors = await _builder.ValidateAsync(Route(1, "bad-host", r => r.MatchHostsCsv = hosts));

        errors.Should().ContainSingle().Which.Should().Contain("Match host");
    }

    [Theory]
    [InlineData("app.localhost")]
    [InlineData("app.localhost:5050")]
    [InlineData("*.localhost")]
    [InlineData("localhost:*")]
    [InlineData("app.localhost, api.localhost:5050")]
    public async Task Ordinary_match_hosts_are_accepted(string hosts)
    {
        (await _builder.ValidateAsync(Route(1, "good-host", r => r.MatchHostsCsv = hosts))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/{id:integer}")]
    [InlineData("/x/{slug:string}")]
    [InlineData("/y/{id:length(abc)}")]
    public async Task Constraints_routing_cannot_resolve_are_refused(string matchPath)
    {
        var errors = await _builder.ValidateAsync(Route(1, "bad-constraint", r => r.MatchPath = matchPath));

        errors.Should().ContainSingle().Which.Should().Contain("constraint");
    }

    [Fact]
    public async Task Known_constraints_are_accepted()
    {
        (await _builder.ValidateAsync(Route(1, "int", r => r.MatchPath = "/api/{id:int}/{**rest}"))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://localhost:5050/")]
    [InlineData("http://127.0.0.1:5050/")]
    [InlineData("http://app.localhost:5050/")]
    public async Task A_destination_on_the_gateway_itself_is_refused(string destination)
    {
        var errors = await _builder.ValidateAsync(Route(1, "loop", r => r.DestinationUrlOverride = destination));

        errors.Should().ContainSingle().Which.Should().Contain("gateway itself");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_timeout_below_one_second_is_refused(int timeout)
    {
        var errors = await _builder.ValidateAsync(Route(1, "timeout", r => r.TimeoutSeconds = timeout));

        errors.Should().ContainSingle().Which.Should().Contain("Timeout");
    }

    [Fact]
    public async Task Build_keeps_the_older_of_two_routes_routing_cannot_tell_apart()
    {
        var result = await _builder.BuildAsync(
        [
            Route(2, "api-v2", r => r.MatchPath = "/api/{**anything}"),
            Route(1, "api", r => r.MatchPath = "/api/{**rest}"),
            Route(3, "api-ordered", r => { r.MatchPath = "/api/{**rest}"; r.Order = 1; }),
            Route(4, "api-host", r => { r.MatchPath = "/api/{**rest}"; r.MatchHostsCsv = "api.localhost"; }),
        ]);

        result.Routes.Select(r => r.RouteId).Should().BeEquivalentTo("route-1", "route-3", "route-4");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("[api-v2]").And.Contain("'api'");
    }

    [Fact]
    public async Task A_route_dropped_by_yarp_does_not_shadow_a_valid_duplicate()
    {
        var result = await _builder.BuildAsync(
        [
            Route(1, "locked", r => { r.MatchPath = "/api/{**rest}"; r.AuthorizationPolicy = "no-such-policy"; }),
            Route(2, "api", r => r.MatchPath = "/api/{**rest}"),
        ]);

        result.Routes.Should().ContainSingle().Which.RouteId.Should().Be("route-2");
    }

    [Fact]
    public async Task Whatever_the_builder_lets_through_leaves_routing_working()
    {
        // Everything here would, unchecked, have broken routing for every endpoint (bad host,
        // bad constraint) or made /api a 500 (ambiguous pair).
        var build = await _builder.BuildAsync(
        [
            Route(1, "url-host", r => { r.MatchPath = "/one/{**rest}"; r.MatchHostsCsv = "http://localhost:5050"; }),
            Route(2, "ipv6-host", r => { r.MatchPath = "/two/{**rest}"; r.MatchHostsCsv = "[::1]"; }),
            Route(3, "bad-constraint", r => r.MatchPath = "/three/{id:integer}"),
            Route(4, "api", r => r.MatchPath = "/api/{**rest}"),
            Route(5, "api-again", r => r.MatchPath = "/api/{**path}"),
            Route(6, "good", r => { r.MatchPath = "/good/{id:int}"; r.MatchHostsCsv = "localhost:*, 127.0.0.1:*"; }),
        ]);
        build.Routes.Select(r => r.RouteId).Should().BeEquivalentTo("route-4", "route-6");

        await using var app = await StartGatewayAsync(build);
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        (await client.GetAsync("/manage-stand-in")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Reaches the proxy (its destination is down), rather than failing as ambiguous.
        (await client.GetAsync("/api/x")).StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        (await client.GetAsync("/good/12")).StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_request_body_over_kestrels_30MB_default_reaches_the_service()
    {
        // The service accepts large uploads; the gateway must not be the one that refuses them.
        var backend = WebApplication.CreateBuilder();
        backend.WebHost.UseUrls("http://127.0.0.1:0");
        backend.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null);
        backend.Logging.ClearProviders();
        await using var service = backend.Build();
        service.MapPost("/upload/{**rest}", async (HttpRequest request) =>
        {
            long total = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(buffer)) > 0) total += read;
            return Results.Text(total.ToString());
        });
        await service.StartAsync();

        var build = await _builder.BuildAsync([Route(1, "upload", r =>
        {
            r.MatchPath = "/upload/{**rest}";
            r.DestinationUrlOverride = Address(service);
            r.TimeoutSeconds = 60;
        })]);
        build.Routes.Should().ContainSingle().Which.MaxRequestBodySize.Should().Be(-1);
        await using var gateway = await StartGatewayAsync(build);
        using var client = new HttpClient { BaseAddress = new Uri(Address(gateway)), Timeout = TimeSpan.FromMinutes(1) };

        const int size = 31 * 1024 * 1024;
        var response = await client.PostAsync("/upload/file", new ByteArrayContent(new byte[size]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(size.ToString());
    }

    private static async Task<WebApplication> StartGatewayAsync(ProxyBuildResult build)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddReverseProxy().LoadFromMemory(build.Routes, build.Clusters);
        var app = builder.Build();
        app.MapGet("/manage-stand-in", () => "ok");
        app.MapReverseProxy();
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static ProxyRoute Route(int id, string name, Action<ProxyRoute>? configure = null)
    {
        var route = new ProxyRoute
        {
            Id = id,
            Name = name,
            Enabled = true,
            MatchPath = $"/r{id}/{{**catch-all}}",
            PathTransformMode = "None",
            // A closed port: requests reach YARP and fail there (502), never inside routing.
            DestinationUrlOverride = "http://localhost:1/",
            TimeoutSeconds = 5,
        };
        configure?.Invoke(route);
        return route;
    }

    private sealed class StaticOptionsMonitor(DevDeckOptions value) : IOptionsMonitor<DevDeckOptions>
    {
        public DevDeckOptions CurrentValue => value;
        public DevDeckOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DevDeckOptions, string?> listener) => null;
    }
}
