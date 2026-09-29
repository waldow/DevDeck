using DevDeck.Web.Services.Proxy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

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
    [InlineData("/{**path}")]
    [InlineData("/{*rest}")]
    [InlineData("/{**catch-all:nonfile}")]
    [InlineData("/{x}/{**rest}")]
    [InlineData("/{x}")]
    [InlineData("/{a}/{b}/{**rest}")]
    public void Any_template_without_literal_text_is_a_catch_all(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeTrue();
        reason.Should().Contain("Catch-all");
    }

    [Theory]
    [InlineData("/{tenant}/app/{**rest}")]
    [InlineData("/{name}.json")]
    public void Parameter_first_templates_with_literal_text_are_not_catch_alls(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Theory]
    [InlineData("manage/{**rest}")] // no leading slash still maps to /manage
    [InlineData("  /MANAGE/x  ")]
    [InlineData("/_devdeck/{**rest}")]
    public void Reserved_prefixes_are_rejected_however_the_template_is_written(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeTrue();
        reason.Should().Contain("reserved");
    }

    [Theory]
    [InlineData("/api/{**rest")]
    [InlineData("/api/{id")]
    [InlineData("/api//x")]
    public void Malformed_templates_are_rejected(string path)
    {
        ReservedPaths.IsReserved(path, out var reason).Should().BeTrue();
        reason.Should().Contain("not a valid route template");
    }

    [Theory]
    [InlineData("/Manage", true)]
    [InlineData("/manage/Services/Edit/1", true)]
    [InlineData("/MANAGE/", true)]
    [InlineData("/css/site.css", true)]
    [InlineData("/favicon.ico", true)]
    [InlineData("/management", false)]
    [InlineData("/app/Manage", false)]
    [InlineData("/", false)]
    public void Request_paths_under_reserved_prefixes_are_detected(string path, bool reserved)
    {
        ReservedPaths.IsReservedRequestPath(new PathString(path)).Should().Be(reserved);
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
