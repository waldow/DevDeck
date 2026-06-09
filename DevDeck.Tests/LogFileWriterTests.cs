using DevDeck.Web.Services.Logs;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class LogFileWriterTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("devdeck-logwriter-");

    public void Dispose() => _temp.Delete(recursive: true);

    [Fact]
    public async Task Append_then_close_flushes_lines_to_disk()
    {
        var writer = new LogFileWriter();
        var path = Path.Combine(_temp.FullName, "run.log");

        writer.Append(path, Line("hello"));
        writer.Append(path, Line("world"));
        writer.Close(path);

        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("hello").And.Contain("world");
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Append_after_close_does_not_throw_and_keeps_writing()
    {
        var writer = new LogFileWriter();
        var path = Path.Combine(_temp.FullName, "run.log");

        writer.Append(path, Line("before"));
        writer.Close(path);
        var act = () => writer.Append(path, Line("after"));

        act.Should().NotThrow();
        writer.Close(path);
        (await File.ReadAllTextAsync(path)).Should().Contain("before").And.Contain("after");
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Append_after_dispose_is_a_noop()
    {
        var writer = new LogFileWriter();
        var path = Path.Combine(_temp.FullName, "run.log");
        writer.Append(path, Line("kept"));
        await writer.DisposeAsync();

        var act = () => writer.Append(path, Line("dropped"));

        act.Should().NotThrow();
        (await File.ReadAllTextAsync(path)).Should().Contain("kept").And.NotContain("dropped");
    }

    [Fact]
    public async Task Concurrent_append_and_close_never_throws()
    {
        // Regression guard: appends arrive on process-output threadpool threads while the
        // exit handler calls Close. An ObjectDisposedException here would be an unhandled
        // exception that kills the host.
        var writer = new LogFileWriter();
        var path = Path.Combine(_temp.FullName, "run.log");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var appenders = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                writer.Append(path, Line($"worker-{worker}-line-{i++}"));
            }
        }));
        var closer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                writer.Close(path);
                await Task.Delay(1);
            }
        });

        var act = async () => await Task.WhenAll(appenders.Append(closer));

        await act.Should().NotThrowAsync();
        await writer.DisposeAsync();
    }

    private static LogLine Line(string text) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DevServiceId = 1,
        ServiceRunId = 1,
        Stream = "OUT",
        Text = text,
    };
}
