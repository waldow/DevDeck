using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;

namespace DevDeck.Tests;

public sealed class ProcessLogBufferTests
{
    [Fact]
    public void Trims_old_lines_once_max_exceeded()
    {
        var buffer = new ProcessLogBuffer(maxLines: 10, trimAmount: 3);

        for (int i = 0; i < 15; i++)
        {
            buffer.Append(1, MakeLine(i));
        }

        var snapshot = buffer.Snapshot(1);
        snapshot.Count.Should().BeLessThanOrEqualTo(15);
        // After 11th append the buffer trimmed 3, so we should keep tail-most lines.
        snapshot.Last().Text.Should().Be("line-14");
        snapshot.Should().HaveCountGreaterThanOrEqualTo(5);
    }

    [Fact]
    public void Returns_empty_for_unknown_service()
    {
        var buffer = new ProcessLogBuffer(maxLines: 100, trimAmount: 10);
        buffer.Snapshot(999).Should().BeEmpty();
    }

    [Fact]
    public void Clear_empties_the_buffer()
    {
        var buffer = new ProcessLogBuffer(maxLines: 100, trimAmount: 10);
        buffer.Append(1, MakeLine(1));
        buffer.Clear(1);
        buffer.Snapshot(1).Should().BeEmpty();
    }

    private static LogLine MakeLine(int i) => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        DevServiceId = 1,
        ServiceRunId = 1,
        Stream = "OUT",
        Text = $"line-{i}",
    };
}
