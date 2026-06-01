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

    public ProxyRouteBuilder(
        ProxyDestinationValidator validator,
        CommandTemplateRenderer? renderer = null,
        IOptionsMonitor<DevDeckOptions>? options = null)
    {
        _validator = validator;
        _renderer = renderer ?? new CommandTemplateRenderer();
        _options = options;
    }

    public ProxyBuildResult Build(IEnumerable<ProxyRoute> routes)
    {
        var routeConfigs = new List<RouteConfig>();
        var clusterConfigs = new List<ClusterConfig>();
        var warnings = new List<string>();

        foreach (var route in routes.Where(r => r.Enabled).OrderBy(r => r.Order))
        {
            var allowCatchAllRoutes = _options?.CurrentValue.ReverseProxy.AllowCatchAllRoutes ?? false;
            if (ReservedPaths.IsReserved(route.MatchPath, out var reservedReason, allowCatchAllRoutes))
            {
                warnings.Add($"[{route.Name}] {reservedReason}");
                continue;
            }

            var destination = ResolveDestination(route);
            if (string.IsNullOrWhiteSpace(destination.Url))
            {
                warnings.Add($"[{route.Name}] No destination URL (link a service or set a destination override).");
                continue;
            }
            if (destination.UnknownPlaceholders.Count > 0)
            {
                warnings.Add($"[{route.Name}] Unknown destination URL placeholder(s): {string.Join(", ", destination.UnknownPlaceholders)}.");
                continue;
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
                warnings.Add($"[{route.Name}] {validation.Error}");
                continue;
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
                AuthorizationPolicy = string.IsNullOrWhiteSpace(route.AuthorizationPolicy) ? null : route.AuthorizationPolicy,
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

            routeConfigs.Add(routeConfig);
            clusterConfigs.Add(clusterConfig);
        }

        return new ProxyBuildResult(routeConfigs, clusterConfigs, warnings);
    }

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

internal sealed record DestinationResolution(string? Url, IReadOnlyList<string> UnknownPlaceholders);
