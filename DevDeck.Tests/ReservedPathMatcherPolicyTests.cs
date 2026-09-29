using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Forwarder;

namespace DevDeck.Tests;

// A proxy route with a low Order (or an allowed catch-all) outranks the Manage controllers in
// endpoint routing; the policy must drop it for reserved paths so the MVC endpoint wins.
public sealed class ReservedPathMatcherPolicyTests
{
    private readonly ReservedPathMatcherPolicy _policy = new();

    [Theory]
    [InlineData("/Manage")]
    [InlineData("/manage/Services/Edit/3")]
    [InlineData("/css/devdeck.css")]
    public async Task Proxy_candidates_are_dropped_for_reserved_paths(string path)
    {
        var candidates = Candidates(out var proxyIndex, out var mvcIndex);

        await _policy.ApplyAsync(Request(path), candidates);

        candidates.IsValidCandidate(proxyIndex).Should().BeFalse();
        candidates.IsValidCandidate(mvcIndex).Should().BeTrue();
    }

    [Theory]
    [InlineData("/app")]
    [InlineData("/api/Manage")]
    [InlineData("/management")]
    public async Task Proxy_candidates_are_kept_for_other_paths(string path)
    {
        var candidates = Candidates(out var proxyIndex, out var mvcIndex);

        await _policy.ApplyAsync(Request(path), candidates);

        candidates.IsValidCandidate(proxyIndex).Should().BeTrue();
        candidates.IsValidCandidate(mvcIndex).Should().BeTrue();
    }

    [Fact]
    public void Applies_only_to_endpoint_sets_that_contain_a_proxy_endpoint()
    {
        _policy.AppliesToEndpoints([MvcEndpoint()]).Should().BeFalse();
        _policy.AppliesToEndpoints([MvcEndpoint(), ProxyEndpoint()]).Should().BeTrue();
    }

    private static CandidateSet Candidates(out int proxyIndex, out int mvcIndex)
    {
        proxyIndex = 0;
        mvcIndex = 1;
        return new CandidateSet(
            [ProxyEndpoint(), MvcEndpoint()],
            [new RouteValueDictionary(), new RouteValueDictionary()],
            [0, 1]);
    }

    private static Endpoint ProxyEndpoint() => new(
        _ => Task.CompletedTask,
        new EndpointMetadataCollection(new RouteModel(new RouteConfig { RouteId = "route-1" }, cluster: null, HttpTransformer.Empty)),
        "proxy");

    private static Endpoint MvcEndpoint() => new(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "mvc");

    private static HttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }
}
