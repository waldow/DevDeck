using DevDeck.Web.Services.Commands;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class CommandTemplateRendererTests
{
    private readonly CommandTemplateRenderer _renderer = new();

    [Fact]
    public void Replaces_known_placeholders()
    {
        var values = CommandTemplateRenderer.BuildValues(7, "frontend", 5173, "/work");
        var result = _renderer.Render("run dev -- --port {port} --name {name}", values);

        result.Text.Should().Be("run dev -- --port 5173 --name frontend");
        result.UnknownPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void Leaves_unknown_placeholders_intact_and_reports_them()
    {
        var values = CommandTemplateRenderer.BuildValues(1, "x", 80, "/w");
        var result = _renderer.Render("{name} {unknown} {also_unknown}", values);

        result.Text.Should().Be("x {unknown} {also_unknown}");
        result.UnknownPlaceholders.Should().BeEquivalentTo(new[] { "unknown", "also_unknown" });
    }

    [Fact]
    public void Handles_null_template()
    {
        var values = CommandTemplateRenderer.BuildValues(1, "n", null, null);
        var result = _renderer.Render(null, values);

        result.Text.Should().Be(string.Empty);
        result.UnknownPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void Null_port_treated_as_unknown_when_referenced()
    {
        var values = CommandTemplateRenderer.BuildValues(1, "n", null, "/w");
        var result = _renderer.Render("--port {port}", values);

        result.Text.Should().Be("--port {port}");
        result.UnknownPlaceholders.Should().BeEquivalentTo(new[] { "port" });
    }
}
