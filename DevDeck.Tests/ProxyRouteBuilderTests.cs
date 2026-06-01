using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Proxy;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class ProxyRouteBuilderTests
{
    private readonly ProxyRouteBuilder _builder = new(new ProxyDestinationValidator(allowExternal: false));

    [Fact]
    public void None_produces_no_path_transforms()
    {
        var route = NewRoute("None");
        var t = ProxyRouteBuilder.BuildTransforms(route);
        t.Any(d => d.ContainsKey("PathRemovePrefix") || d.ContainsKey("PathPrefix") || d.ContainsKey("PathSet")).Should().BeFalse();
    }

    [Fact]
    public void RemovePrefix_produces_PathRemovePrefix()
    {
        var route = NewRoute("RemovePrefix", removePrefix: "/app");
        var t = ProxyRouteBuilder.BuildTransforms(route);
        FindValue(t, "PathRemovePrefix").Should().Be("/app");
    }

    [Fact]
    public void AddPrefix_produces_PathPrefix()
    {
        var route = NewRoute("AddPrefix", addPrefix: "/api");
        var t = ProxyRouteBuilder.BuildTransforms(route);
        FindValue(t, "PathPrefix").Should().Be("/api");
    }

    [Fact]
    public void RemoveAndAddPrefix_produces_both_in_order()
    {
        var route = NewRoute("RemoveAndAddPrefix", removePrefix: "/functions", addPrefix: "/api");
        var t = ProxyRouteBuilder.BuildTransforms(route);
        t.Select(d => d.Keys.FirstOrDefault())
            .Where(k => k is "PathRemovePrefix" or "PathPrefix")
            .Should().BeEquivalentTo(new[] { "PathRemovePrefix", "PathPrefix" }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void SetPath_produces_PathSet()
    {
        var route = NewRoute("SetPath", pathSet: "/api/v1/echo");
        var t = ProxyRouteBuilder.BuildTransforms(route);
        FindValue(t, "PathSet").Should().Be("/api/v1/echo");
    }

    private static string? FindValue(IReadOnlyList<IReadOnlyDictionary<string, string>> transforms, string key)
    {
        foreach (var t in transforms)
        {
            if (t.TryGetValue(key, out var v)) return v;
        }
        return null;
    }

    [Fact]
    public void Build_skips_route_with_reserved_match_path_and_emits_warning()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Name = "BadOne",
                Enabled = true,
                MatchPath = "/Manage/Services/{**catch-all}",
                PathTransformMode = "None",
                DestinationUrlOverride = "http://localhost:1234/",
            },
        };
        var result = _builder.Build(routes);
        result.Routes.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("BadOne") && w.Contains("Manage"));
    }

    [Fact]
    public void Build_skips_route_with_no_destination_and_emits_warning()
    {
        var routes = new[]
        {
            new ProxyRoute { Name = "NoDest", Enabled = true, MatchPath = "/app/{**catch-all}", PathTransformMode = "None" },
        };
        var result = _builder.Build(routes);
        result.Routes.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("NoDest"));
    }

    [Fact]
    public void Build_produces_route_and_cluster_for_valid_input()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Name = "Good", Enabled = true,
                MatchPath = "/app/{**catch-all}",
                PathTransformMode = "RemovePrefix",
                PathPrefixToRemove = "/app",
                DestinationUrlOverride = "http://localhost:5173/",
            },
        };
        var result = _builder.Build(routes);
        result.Routes.Should().ContainSingle();
        result.Clusters.Should().ContainSingle();
    }

    [Fact]
    public void Build_renders_linked_service_url_template_before_validation()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 12,
                Name = "Templated",
                Enabled = true,
                DevServiceId = 7,
                DestinationUrlOverride = "",
                DevService = new DevService
                {
                    Id = 7,
                    Name = "Main site",
                    ServiceType = "Vite",
                    WorkingDirectory = "C:\\work\\main",
                    StartCommand = "npm",
                    Url = "http://localhost:{port}",
                    Port = 5173,
                },
                MatchPath = "/app/{**catch-all}",
                PathTransformMode = "None",
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().ContainSingle();
        result.Clusters.Should().ContainSingle();
        result.Clusters[0].Destinations!["destination-0"].Address.Should().Be("http://localhost:5173/");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Build_skips_linked_service_url_template_when_port_placeholder_cannot_be_rendered()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 12,
                Name = "MissingPort",
                Enabled = true,
                DevServiceId = 7,
                DevService = new DevService
                {
                    Id = 7,
                    Name = "Main site",
                    ServiceType = "Vite",
                    WorkingDirectory = "C:\\work\\main",
                    StartCommand = "npm",
                    Url = "http://localhost:{port}",
                },
                MatchPath = "/app/{**catch-all}",
                PathTransformMode = "None",
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("MissingPort") && w.Contains("port"));
    }

    [Fact]
    public void Build_adds_devdeck_metadata_for_linked_service_routes()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 42,
                Name = "Good",
                Enabled = true,
                DevServiceId = 7,
                MatchPath = "/app/{**catch-all}",
                PathTransformMode = "None",
                DestinationUrlOverride = "http://localhost:5173/",
                AutoStartService = true,
                RequireHealthyDestination = true,
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().ContainSingle();
        var metadata = result.Routes[0].Metadata;
        metadata.Should().NotBeNull();
        metadata!["DevDeck.ServiceId"].Should().Be("7");
        metadata["DevDeck.AutoStartService"].Should().Be("True");
        metadata["DevDeck.RequireHealthyDestination"].Should().Be("True");
        metadata["DevDeck.DestinationHost"].Should().Be("localhost");
        metadata["DevDeck.DestinationPort"].Should().Be("5173");
    }

    [Fact]
    public void Build_adds_destination_host_metadata_from_destination_url()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 42,
                Name = "PrivateHost",
                Enabled = true,
                DevServiceId = 7,
                MatchPath = "/api/{**catch-all}",
                PathTransformMode = "None",
                DestinationUrlOverride = "http://192.168.1.50:5000/",
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().ContainSingle();
        var metadata = result.Routes[0].Metadata!;
        metadata["DevDeck.DestinationHost"].Should().Be("192.168.1.50");
        metadata["DevDeck.DestinationPort"].Should().Be("5000");
    }

    [Fact]
    public void Build_adds_destination_port_metadata_from_rendered_service_url_template()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 12,
                Name = "Templated",
                Enabled = true,
                DevServiceId = 7,
                DevService = new DevService
                {
                    Id = 7,
                    Name = "Main site",
                    ServiceType = "Vite",
                    WorkingDirectory = "C:\\work\\main",
                    StartCommand = "npm",
                    Url = "http://localhost:{port}",
                    Port = 5173,
                },
                MatchPath = "/app/{**catch-all}",
                PathTransformMode = "None",
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().ContainSingle();
        result.Routes[0].Metadata!["DevDeck.DestinationHost"].Should().Be("localhost");
        result.Routes[0].Metadata!["DevDeck.DestinationPort"].Should().Be("5173");
    }

    [Fact]
    public void Build_renders_external_port_when_service_uses_external_instance()
    {
        var routes = new[]
        {
            new ProxyRoute
            {
                Id = 12,
                Name = "Passthru",
                Enabled = true,
                DevServiceId = 7,
                DevService = new DevService
                {
                    Id = 7,
                    Name = "Functions",
                    ServiceType = "AzureFunction",
                    WorkingDirectory = "C:\\work\\fn",
                    StartCommand = "func",
                    Url = "http://localhost:{port}",
                    Port = 7072,                  // DevDeck-managed port
                    UseExternalInstance = true,
                    ExternalPort = 7071,          // VS debug host port we passthru to
                },
                MatchPath = "/api/{**catch-all}",
                PathTransformMode = "None",
            },
        };

        var result = _builder.Build(routes);

        result.Routes.Should().ContainSingle();
        // The destination resolves to the external port, not the managed port.
        result.Routes[0].Metadata!["DevDeck.DestinationPort"].Should().Be("7071");
        result.Clusters[0].Destinations!["destination-0"].Address.Should().Be("http://localhost:7071/");
    }

    private static ProxyRoute NewRoute(string mode, string? removePrefix = null, string? addPrefix = null, string? pathSet = null) => new()
    {
        Name = "r",
        Enabled = true,
        MatchPath = "/x/{**catch-all}",
        PathTransformMode = mode,
        PathPrefixToRemove = removePrefix,
        PathPrefixToAdd = addPrefix,
        PathSet = pathSet,
    };
}
