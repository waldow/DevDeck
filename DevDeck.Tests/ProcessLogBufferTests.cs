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

    [Fact]
    public void SnapshotSince_keeps_returning_new_lines_across_a_trim()
    {
        // Regression: the live-log cursor used to be a line count, which a trim shrinks — the
        // viewer then skipped every new line until the count grew back past its cursor.
        var buffer = new ProcessLogBuffer(maxLines: 10, trimAmount: 5);
        for (var i = 0; i < 9; i++) buffer.Append(1, MakeLine(i));
        var first = buffer.SnapshotSince(1, since: 0);
        first.Lines.Should().HaveCount(9);

        for (var i = 9; i < 13; i++) buffer.Append(1, MakeLine(i)); // trims 5 at the 11th line

        var next = buffer.SnapshotSince(1, first.Next);
        next.Lines.Select(l => l.Text).Should().Equal("line-9", "line-10", "line-11", "line-12");
        next.Dropped.Should().Be(0);
        next.Reset.Should().BeFalse();
        buffer.SnapshotSince(1, next.Next).Lines.Should().BeEmpty();
    }

    [Fact]
    public void SnapshotSince_reports_lines_trimmed_before_the_reader_saw_them()
    {
        var buffer = new ProcessLogBuffer(maxLines: 10, trimAmount: 5);
        buffer.Append(1, MakeLine(0));
        var cursor = buffer.SnapshotSince(1, 0).Next; // 1

        for (var i = 1; i < 12; i++) buffer.Append(1, MakeLine(i)); // lines 0-4 trimmed

        var slice = buffer.SnapshotSince(1, cursor);
        slice.Dropped.Should().Be(4); // lines 1-4 were never delivered
        slice.Lines.First().Text.Should().Be("line-5");
        slice.Lines.Last().Text.Should().Be("line-11");
    }

    [Fact]
    public void SnapshotSince_resets_a_cursor_from_before_a_restart()
    {
        var buffer = new ProcessLogBuffer(maxLines: 100, trimAmount: 10);
        buffer.Append(1, MakeLine(0));

        var slice = buffer.SnapshotSince(1, since: 500);

        slice.Reset.Should().BeTrue();
        slice.Lines.Should().ContainSingle();
        buffer.SnapshotSince(2, since: 500).Reset.Should().BeTrue();
        buffer.SnapshotSince(2, since: 0).Reset.Should().BeFalse();
    }

    [Fact]
    public void Clear_keeps_cursors_valid()
    {
        var buffer = new ProcessLogBuffer(maxLines: 100, trimAmount: 10);
        buffer.Append(1, MakeLine(0));
        var cursor = buffer.SnapshotSince(1, 0).Next;
        buffer.Clear(1);
        buffer.Append(1, MakeLine(1));

        var slice = buffer.SnapshotSince(1, cursor);

        slice.Reset.Should().BeFalse();
        slice.Lines.Should().ContainSingle().Which.Text.Should().Be("line-1");
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
