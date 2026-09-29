using Microsoft.AspNetCore.Routing.Matching;
using Yarp.ReverseProxy.Model;

namespace DevDeck.Web.Services.Proxy;

/// <summary>
/// Request-time enforcement of <see cref="ReservedPaths"/>: removes proxy endpoints from the
/// routing candidates of any request under a reserved prefix, so /Manage and DevDeck's static
/// assets are never proxied. Endpoint routing ranks candidates by Order before specificity, so a
/// proxy route with a low Order (or an allowed catch-all) would otherwise outrank the Manage
/// controllers; dropping it here lets the MVC endpoint win, or 404s if nothing else matches.
/// </summary>
public sealed class ReservedPathMatcherPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            if (IsProxyEndpoint(endpoint))
            {
                return true;
            }
        }

        return false;
    }

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        if (!ReservedPaths.IsReservedRequestPath(httpContext.Request.Path))
        {
            return Task.CompletedTask;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates.IsValidCandidate(i) && IsProxyEndpoint(candidates[i].Endpoint))
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsProxyEndpoint(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<RouteModel>() is not null;
}
