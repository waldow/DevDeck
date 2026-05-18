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
