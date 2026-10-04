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
    public void Shell_variable_syntax_is_not_a_placeholder()
    {
        // ${PORT} is the shell's own expansion; matching it case-insensitively turned it into $5173.
        var values = CommandTemplateRenderer.BuildValues(1, "api", 5173, "/w");
        var result = _renderer.Render("sh -c \"serve --port ${PORT} --name {name}\"", values);

        result.Text.Should().Be("sh -c \"serve --port ${PORT} --name api\"");
        result.UnknownPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public void Arguments_quote_a_value_with_spaces_so_it_stays_one_argument()
    {
        var values = CommandTemplateRenderer.BuildValues(1, "My App", 5173, @"C:\Users\jdoe\OneDrive - Contoso\api");
        var result = _renderer.RenderArguments("args.sh --cwd {workingDirectory} --name {name} --port {port}", values);

        result.Text.Should().Be(@"args.sh --cwd ""C:\Users\jdoe\OneDrive - Contoso\api"" --name ""My App"" --port 5173");
        SplitArguments(result.Text).Should().Equal(
            "args.sh", "--cwd", @"C:\Users\jdoe\OneDrive - Contoso\api", "--name", "My App", "--port", "5173");
    }

    [Fact]
    public void Arguments_escape_a_value_the_template_already_quotes()
    {
        // A trailing backslash before the template's closing quote would otherwise escape it
        // and swallow the rest of the line into one argument.
        var values = CommandTemplateRenderer.BuildValues(1, "n", 5173, @"C:\src\app\");
        var result = _renderer.RenderArguments(@"--cwd ""{workingDirectory}"" --port {port}", values);

        SplitArguments(result.Text).Should().Equal("--cwd", @"C:\src\app\", "--port", "5173");
    }

    [Fact]
    public void Arguments_leave_simple_values_unquoted()
    {
        var values = CommandTemplateRenderer.BuildValues(7, "frontend", 5173, "/work");
        _renderer.RenderArguments("run dev -- --port {port} --name {name}", values).Text
            .Should().Be("run dev -- --port 5173 --name frontend");
    }

    [Fact]
    public void Rendered_arguments_reach_the_process_as_separate_arguments()
    {
        if (OperatingSystem.IsWindows()) return;

        var values = CommandTemplateRenderer.BuildValues(1, "My \"quoted\" App", 5173, "/tmp/devdeck probe dir");
        var arguments = _renderer.RenderArguments(@"""[%s]\n"" {workingDirectory} {name} {port}", values).Text;
        using var printf = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("printf", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = printf.StandardOutput.ReadToEnd();
        printf.WaitForExit();

        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("[/tmp/devdeck probe dir]", "[My \"quoted\" App]", "[5173]");
    }

    // How ProcessStartInfo.Arguments is split into argv: the Windows C runtime rules, which .NET
    // applies on every OS.
    private static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasArgument = false;
        var i = 0;
        while (i < arguments.Length)
        {
            var backslashes = 0;
            while (i < arguments.Length && arguments[i] == '\\') { backslashes++; i++; }
            if (backslashes > 0)
            {
                hasArgument = true;
                if (i < arguments.Length && arguments[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) { current.Append('"'); i++; }
                }
                else
                {
                    current.Append('\\', backslashes);
                }
                continue;
            }

            var c = arguments[i];
            if (c == '"')
            {
                hasArgument = true;
                if (inQuotes && i + 1 < arguments.Length && arguments[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (hasArgument) { result.Add(current.ToString()); current.Clear(); hasArgument = false; }
            }
            else
            {
                current.Append(c);
                hasArgument = true;
            }
            i++;
        }
        if (hasArgument) result.Add(current.ToString());
        return result;
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
