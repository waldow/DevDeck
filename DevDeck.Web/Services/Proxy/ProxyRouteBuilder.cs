using System.Diagnostics.CodeAnalysis;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace DevDeck.Web.Services.Proxy;

public sealed class ProxyRouteBuilder
{
    private readonly ProxyDestinationValidator _validator;
    private readonly CommandTemplateRenderer _renderer;
    private readonly IOptionsMonitor<DevDeckOptions>? _options;
    private readonly IConfigValidator? _yarpValidator;
    private readonly ParameterPolicyFactory? _parameterPolicyFactory;

    public ProxyRouteBuilder(
        ProxyDestinationValidator validator,
        CommandTemplateRenderer? renderer = null,
        IOptionsMonitor<DevDeckOptions>? options = null,
        IConfigValidator? yarpValidator = null,
        ParameterPolicyFactory? parameterPolicyFactory = null)
    {
        _validator = validator;
        _renderer = renderer ?? new CommandTemplateRenderer();
        _options = options;
        _yarpValidator = yarpValidator;
        _parameterPolicyFactory = parameterPolicyFactory;
    }

    /// <summary>
    /// Builds the YARP snapshot from DevDeck's own rules (reserved paths, catch-alls,
    /// destinations, hosts and constraints routing can't parse, routes that would be
    /// ambiguous with an earlier one). Routes that break a rule are skipped with a warning.
    /// </summary>
    public ProxyBuildResult Build(IEnumerable<ProxyRoute> routes) =>
        // Without YARP validation nothing in the build awaits, so this completes synchronously.
        BuildCoreAsync(routes, validateWithYarp: false).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="Build"/>, plus YARP's own config validation per route. YARP rejects a whole
    /// snapshot when any single route in it is invalid (bad template, empty host, unknown
    /// authorization policy...) — which would silently freeze every later route edit and make
    /// the initial load throw at startup — so invalid routes are dropped here, with a warning.
    /// </summary>
    public Task<ProxyBuildResult> BuildAsync(IEnumerable<ProxyRoute> routes) =>
        BuildCoreAsync(routes, validateWithYarp: _yarpValidator is not null);

    private async Task<ProxyBuildResult> BuildCoreAsync(IEnumerable<ProxyRoute> routes, bool validateWithYarp)
    {
        var routeConfigs = new List<RouteConfig>();
        var clusterConfigs = new List<ClusterConfig>();
        var warnings = new List<string>();
        var included = new List<ProxyRoute>();

        foreach (var route in routes.Where(r => r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Id))
        {
            if (!TryBuild(route, out var built, out var error))
            {
                warnings.Add($"[{route.Name}] {error}");
                continue;
            }

            if (validateWithYarp && await ValidateWithYarpAsync(built) is { Count: > 0 } yarpErrors)
            {
                warnings.Add($"[{route.Name}] {string.Join(" ", yarpErrors)}");
                continue;
            }

            // Two routes endpoint routing can't tell apart turn every request they match into
            // a 500, so keep the older one and skip the other. Only routes that made it into the
            // snapshot count: one dropped above must not shadow a valid duplicate.
            if (ProxyRouteConflicts.FindConflict(route, included) is { } conflict)
            {
                warnings.Add($"[{route.Name}] Skipped: {ProxyRouteConflicts.Describe(conflict)}");
                continue;
            }

            included.Add(route);
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

        // Hosts and constraints are only checked by routing when it builds its matcher, which
        // is shared by every endpoint: one it can't build breaks all routing, /Manage included.
        if (ValidateMatchHosts(route.MatchHostsCsv) is { } hostError)
        {
            error = hostError;
            return false;
        }
        if (ValidateConstraints(route.MatchPath) is { } constraintError)
        {
            error = constraintError;
            return false;
        }

        // "Default" requires an authenticated user, but DevDeck registers no authentication
        // scheme, so the challenge would throw and every request to the route would get a 500.
        if (string.Equals(route.AuthorizationPolicy?.Trim(), "Default", StringComparison.OrdinalIgnoreCase))
        {
            error = "Authorization policy 'Default' requires an authenticated user, but DevDeck has no authentication, " +
                    "so every request would fail. Leave the policy blank (or use 'Anonymous').";
            return false;
        }

        if (route.TimeoutSeconds is <= 0)
        {
            error = "Timeout must be at least 1 second (leave it blank for YARP's default).";
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

        if (destinationUri is not null && PointsAtGateway(destinationUri))
        {
            error = $"Destination {destinationUri.GetLeftPart(UriPartial.Authority)} is the DevDeck gateway itself, " +
                    "so each request would be proxied back into the gateway in a loop.";
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
            // No gateway-side body limit (Kestrel's default is 30 MB): an upload the service itself
            // accepts must not fail only when it goes through the gateway. The service enforces its own.
            MaxRequestBodySize = -1,
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
        var parts = ProxyRouteConflicts.ParseHosts(hostsCsv);
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>
    /// Runs the hosts through the framework's own HostMatcherPolicy, the component that would
    /// otherwise reject them while building the shared endpoint matcher. It accepts "host",
    /// "host:port" and "host:*" — not a URL ("http://localhost:5050") or a bracketed IPv6
    /// literal ("[::1]").
    /// </summary>
    internal static string? ValidateMatchHosts(string? hostsCsv)
    {
        var hosts = ProxyRouteConflicts.ParseHosts(hostsCsv);
        INodeBuilderPolicy policy = new HostMatcherPolicy();
        foreach (var host in hosts)
        {
            // A pasted URL with a path parses as a host routing will simply never match.
            var valid = host.IndexOf('/') < 0;
            if (valid)
            {
                var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new HostAttribute(host)), host);
                try
                {
                    policy.GetEdges([endpoint]);
                }
                catch (InvalidOperationException)
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                return $"Match host '{host}' is not valid: use a host name with an optional port, " +
                       "like 'app.localhost' or 'app.localhost:5050' (no scheme or path).";
            }
        }

        return null;
    }

    // Constraints are resolved by name ("{id:int}") only when routing builds its matcher; an
    // unknown name or bad argument ("{id:integer}", "{id:length(abc)}") would throw there.
    private string? ValidateConstraints(string matchPath)
    {
        if (_parameterPolicyFactory is null)
        {
            return null;
        }

        RoutePattern pattern;
        try
        {
            pattern = RoutePatternFactory.Parse(matchPath.Trim());
        }
        catch (RoutePatternException ex)
        {
            return $"Match path is not a valid route template: {ex.Message}";
        }

        foreach (var parameter in pattern.Parameters)
        {
            foreach (var reference in parameter.ParameterPolicies)
            {
                try
                {
                    _parameterPolicyFactory.Create(parameter, reference);
                }
                catch (Exception ex)
                {
                    return $"Match path constraint '{reference.Content}' on '{{{parameter.Name}}}' is not valid: {ex.Message}";
                }
            }
        }

        return null;
    }

    // A loopback destination on the gateway's own port is the gateway: forwarding to it would
    // match the same route again, endlessly.
    private bool PointsAtGateway(Uri destination)
    {
        var configured = _options?.CurrentValue.ReverseProxy.GatewayBaseUrl;
        if (string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(GatewayUrlResolver.Normalize(configured), UriKind.Absolute, out var gateway) ||
            destination.Port != gateway.Port)
        {
            return false;
        }

        var gatewayListensLocally = ProxyDestinationValidator.IsLocalHost(gateway.Host) || GatewayUrlResolver.IsAnyAddress(gateway.Host);
        return string.Equals(destination.Host, gateway.Host, StringComparison.OrdinalIgnoreCase) ||
               (ProxyDestinationValidator.IsLocalHost(destination.Host) && gatewayListensLocally);
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
