using System.Diagnostics.CodeAnalysis;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace DevDeck.Web.Services.Proxy;

public sealed class ProxyRouteBuilder
{
    private readonly ProxyDestinationValidator _validator;
    private readonly CommandTemplateRenderer _renderer;
    private readonly IOptionsMonitor<DevDeckOptions>? _options;
    private readonly IConfigValidator? _yarpValidator;

    public ProxyRouteBuilder(
        ProxyDestinationValidator validator,
        CommandTemplateRenderer? renderer = null,
        IOptionsMonitor<DevDeckOptions>? options = null,
        IConfigValidator? yarpValidator = null)
    {
        _validator = validator;
        _renderer = renderer ?? new CommandTemplateRenderer();
        _options = options;
        _yarpValidator = yarpValidator;
    }

    /// <summary>
    /// Builds the YARP snapshot from DevDeck's own rules (reserved paths, catch-alls,
    /// destinations). Routes that break a rule are skipped with a warning.
    /// </summary>
    public ProxyBuildResult Build(IEnumerable<ProxyRoute> routes)
    {
        var routeConfigs = new List<RouteConfig>();
        var clusterConfigs = new List<ClusterConfig>();
        var warnings = new List<string>();

        foreach (var route in routes.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            if (TryBuild(route, out var built, out var error))
            {
                routeConfigs.Add(built.Route);
                clusterConfigs.Add(built.Cluster);
            }
            else
            {
                warnings.Add($"[{route.Name}] {error}");
            }
        }

        return new ProxyBuildResult(routeConfigs, clusterConfigs, warnings);
    }

    /// <summary>
    /// <see cref="Build"/>, then YARP's own config validation per route. YARP rejects a whole
    /// snapshot when any single route in it is invalid (bad template, empty host, unknown
    /// authorization policy...) — which would silently freeze every later route edit and make
    /// the initial load throw at startup — so invalid routes are dropped here, with a warning.
    /// </summary>
    public async Task<ProxyBuildResult> BuildAsync(IEnumerable<ProxyRoute> routes)
    {
        var build = Build(routes);
        if (_yarpValidator is null)
        {
            return build;
        }

        var routeConfigs = new List<RouteConfig>();
        var clusterConfigs = new List<ClusterConfig>();
        var warnings = build.Warnings.ToList();
        for (var i = 0; i < build.Routes.Count; i++)
        {
            var built = new BuiltRoute(build.Routes[i], build.Clusters[i]);
            var errors = await ValidateWithYarpAsync(built);
            if (errors.Count > 0)
            {
                warnings.Add($"[{RouteName(built.Route)}] {string.Join(" ", errors)}");
                continue;
            }
            routeConfigs.Add(built.Route);
            clusterConfigs.Add(built.Cluster);
        }

        return new ProxyBuildResult(routeConfigs, clusterConfigs, warnings);
    }

    /// <summary>
    /// Everything that would keep <paramref name="route"/> out of the snapshot, whether or not
    /// it is currently enabled. Used by the route editor and importer so a bad route is refused
    /// up front instead of being saved and then silently skipped. Needs
    /// <see cref="ProxyRoute.DevService"/> loaded when the route links a service.
    /// </summary>
    public async Task<IReadOnlyList<string>> ValidateAsync(ProxyRoute route)
    {
        if (!TryBuild(route, out var built, out var error))
        {
            return [error];
        }

        return _yarpValidator is null ? [] : await ValidateWithYarpAsync(built);
    }

    private bool TryBuild(
        ProxyRoute route,
        [NotNullWhen(true)] out BuiltRoute? built,
        [NotNullWhen(false)] out string? error)
    {
        built = null;
        var allowCatchAllRoutes = _options?.CurrentValue.ReverseProxy.AllowCatchAllRoutes ?? false;
        if (ReservedPaths.IsReserved(route.MatchPath, out var reservedReason, allowCatchAllRoutes))
        {
            error = reservedReason;
            return false;
        }

        var destination = ResolveDestination(route);
        if (string.IsNullOrWhiteSpace(destination.Url))
        {
            error = "No destination URL (link a service or set a destination override).";
            return false;
        }
        if (destination.UnknownPlaceholders.Count > 0)
        {
            error = $"Unknown destination URL placeholder(s): {string.Join(", ", destination.UnknownPlaceholders)}.";
            return false;
        }

        var destinationUrl = NormalizeDestination(destination.Url);
        var destinationUri = Uri.TryCreate(destinationUrl, UriKind.Absolute, out var parsedDestination)
            ? parsedDestination
            : null;
        var destinationHost = destinationUri?.Host;
        var destinationPort = destinationUri?.Port;

        var validation = _validator.Validate(destinationUrl);
        if (!validation.IsValid)
        {
            error = validation.Error ?? "Invalid destination URL.";
            return false;
        }

        var clusterId = $"cluster-{route.Id}";
        var routeId = $"route-{route.Id}";

        var routeConfig = new RouteConfig
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Order = route.Order,
            Match = new RouteMatch
            {
                Path = route.MatchPath,
                Hosts = ParseHosts(route.MatchHostsCsv),
            },
            Transforms = BuildTransforms(route),
            AuthorizationPolicy = string.IsNullOrWhiteSpace(route.AuthorizationPolicy) ? null : route.AuthorizationPolicy.Trim(),
            Metadata = BuildMetadata(route, destinationHost, destinationPort),
        };

        var clusterConfig = new ClusterConfig
        {
            ClusterId = clusterId,
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["destination-0"] = new() { Address = destinationUrl },
            },
            HttpRequest = new Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig
            {
                ActivityTimeout = route.TimeoutSeconds is int ts ? TimeSpan.FromSeconds(ts) : null,
            },
        };

        built = new BuiltRoute(routeConfig, clusterConfig);
        error = null;
        return true;
    }

    private async Task<IReadOnlyList<string>> ValidateWithYarpAsync(BuiltRoute built)
    {
        // YARP's messages name the generated ids ("route-12"); show the route's name instead.
        var name = RouteName(built.Route);
        var errors = new List<string>();
        try
        {
            foreach (var ex in await _yarpValidator!.ValidateRouteAsync(built.Route))
            {
                errors.Add(ex.Message.Replace($"'{built.Route.RouteId}'", $"'{name}'"));
            }
            foreach (var ex in await _yarpValidator.ValidateClusterAsync(built.Cluster))
            {
                errors.Add(ex.Message.Replace($"'{built.Cluster.ClusterId}'", $"'{name}'"));
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }

    private static string RouteName(RouteConfig route) =>
        route.Metadata is not null && route.Metadata.TryGetValue("DevDeck.RouteName", out var name) ? name : route.RouteId;

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> BuildTransforms(ProxyRoute route)
    {
        var transforms = new List<IReadOnlyDictionary<string, string>>();

        switch (route.PathTransformMode)
        {
            case "None":
                break;
            case "RemovePrefix":
                if (!string.IsNullOrEmpty(route.PathPrefixToRemove))
                {
                    transforms.Add(new Dictionary<string, string> { ["PathRemovePrefix"] = route.PathPrefixToRemove });
                }
                break;
            case "AddPrefix":
                if (!string.IsNullOrEmpty(route.PathPrefixToAdd))
                {
                    transforms.Add(new Dictionary<string, string> { ["PathPrefix"] = route.PathPrefixToAdd });
                }
                break;
            case "RemoveAndAddPrefix":
                if (!string.IsNullOrEmpty(route.PathPrefixToRemove))
                {
                    transforms.Add(new Dictionary<string, string> { ["PathRemovePrefix"] = route.PathPrefixToRemove });
                }
                if (!string.IsNullOrEmpty(route.PathPrefixToAdd))
                {
                    transforms.Add(new Dictionary<string, string> { ["PathPrefix"] = route.PathPrefixToAdd });
                }
                break;
            case "SetPath":
                if (!string.IsNullOrEmpty(route.PathSet))
                {
                    transforms.Add(new Dictionary<string, string> { ["PathSet"] = route.PathSet });
                }
                break;
        }

        transforms.Add(new Dictionary<string, string>
        {
            ["RequestHeaderOriginalHost"] = route.PreserveHostHeader ? "true" : "false",
        });

        return transforms;
    }

    private static IReadOnlyList<string>? ParseHosts(string? hostsCsv)
    {
        if (string.IsNullOrWhiteSpace(hostsCsv)) return null;
        var parts = hostsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static string NormalizeDestination(string url)
    {
        return url.EndsWith('/') ? url : url + "/";
    }

    private DestinationResolution ResolveDestination(ProxyRoute route)
    {
        var url = !string.IsNullOrWhiteSpace(route.DestinationUrlOverride)
            ? route.DestinationUrlOverride
            : route.DevService?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return new DestinationResolution(url, Array.Empty<string>());
        }

        if (route.DevService is null)
        {
            return new DestinationResolution(url, Array.Empty<string>());
        }

        var service = route.DevService;
        var rendered = _renderer.Render(
            url,
            CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.EffectivePort, service.WorkingDirectory));

        return new DestinationResolution(rendered.Text, rendered.UnknownPlaceholders);
    }

    private static IReadOnlyDictionary<string, string> BuildMetadata(ProxyRoute route, string? destinationHost, int? destinationPort)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DevDeck.RouteId"] = route.Id.ToString(),
            ["DevDeck.RouteName"] = route.Name,
            ["DevDeck.AutoStartService"] = route.AutoStartService.ToString(),
            ["DevDeck.RequireHealthyDestination"] = route.RequireHealthyDestination.ToString(),
        };

        if (route.DevServiceId is int serviceId)
        {
            metadata["DevDeck.ServiceId"] = serviceId.ToString();
        }

        if (route.DevService is not null)
        {
            metadata["DevDeck.UseExternalInstance"] = route.DevService.UseExternalInstance.ToString();
        }

        if (!string.IsNullOrWhiteSpace(destinationHost))
        {
            metadata["DevDeck.DestinationHost"] = destinationHost;
        }

        if (destinationPort is int port)
        {
            metadata["DevDeck.DestinationPort"] = port.ToString();
        }

        return metadata;
    }
}

public sealed record ProxyBuildResult(
    IReadOnlyList<RouteConfig> Routes,
    IReadOnlyList<ClusterConfig> Clusters,
    IReadOnlyList<string> Warnings);

internal sealed record BuiltRoute(RouteConfig Route, ClusterConfig Cluster);

internal sealed record DestinationResolution(string? Url, IReadOnlyList<string> UnknownPlaceholders);
