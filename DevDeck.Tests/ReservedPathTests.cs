using DevDeck.Web.Services.Proxy;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class ReservedPathTests
{
    [Theory]
    [InlineData("/Manage")]
    [InlineData("/manage")]
    [InlineData("/Manage/Services")]
    [InlineData("/css")]
    [InlineData("/js")]
    [InlineData("/lib")]
    [InlineData("/images")]
    [InlineData("/favicon.ico")]
    [InlineData("/_devdeck")]
    public void Reserved_prefixes_are_rejected(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeTrue();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("/{**catch-all}")]
    [InlineData("/{*catch-all}")]
    [InlineData("/")]
    public void Catch_all_rejected_without_override(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeTrue();
        reason.Should().Contain("Catch-all");
    }

    [Theory]
    [InlineData("/{**catch-all}")]
    [InlineData("/{*catch-all}")]
    [InlineData("/")]
    public void Catch_all_accepted_with_override(string path)
    {
        ReservedPaths.IsReserved(path, out var reason, allowCatchAllRoutes: true).Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/app/{**catch-all}")]
    [InlineData("/api/{**catch-all}")]
    [InlineData("/functions/{**catch-all}")]
    [InlineData("/management/x")] // "management" doesn't collide with /manage exactly
    public void Safe_paths_accepted(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeFalse();
        reason.Should().BeEmpty();
    }
}
