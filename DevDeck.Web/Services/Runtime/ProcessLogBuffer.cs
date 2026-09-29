using System.Collections.Concurrent;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Logs;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Runtime;

public sealed class ProcessLogBuffer
{
    private readonly ConcurrentDictionary<int, RingBuffer> _buffers = new();
    private readonly int _maxLines;
    private readonly int _trimAmount;

    public ProcessLogBuffer(IOptions<DevDeckOptions> options)
    {
        _maxLines = Math.Max(100, options.Value.MaxLiveLogLinesPerService);
        _trimAmount = Math.Max(1, Math.Min(options.Value.LogTrimAmount, _maxLines / 2));
    }

    public ProcessLogBuffer(int maxLines, int trimAmount)
    {
        _maxLines = maxLines;
        _trimAmount = trimAmount;
    }

    public void Append(int serviceId, LogLine line)
    {
        var buffer = _buffers.GetOrAdd(serviceId, _ => new RingBuffer(_maxLines, _trimAmount));
        buffer.Append(line);
    }

    public IReadOnlyList<LogLine> Snapshot(int serviceId)
    {
        return _buffers.TryGetValue(serviceId, out var buffer)
            ? buffer.Snapshot()
            : Array.Empty<LogLine>();
    }

    /// <summary>
    /// Lines appended after sequence number <paramref name="since"/> (the <see cref="LiveLogSlice.Next"/>
    /// of a previous call; 0 for everything). A line count can't serve as this cursor: trimming
    /// the front of the buffer shrinks the count, and a count-based cursor then points past the
    /// end and skips new lines until the count catches up.
    /// </summary>
    public LiveLogSlice SnapshotSince(int serviceId, long since)
    {
        return _buffers.TryGetValue(serviceId, out var buffer)
            ? buffer.Since(since)
            : new LiveLogSlice(Array.Empty<LogLine>(), Next: 0, Dropped: 0, Reset: since > 0);
    }

    public void Clear(int serviceId)
    {
        if (_buffers.TryGetValue(serviceId, out var buffer))
        {
            buffer.Clear();
        }
    }

    private sealed class RingBuffer
    {
        private readonly object _lock = new();
        private readonly List<LogLine> _lines;
        private readonly int _maxLines;
        private readonly int _trimAmount;

        // Sequence number the next appended line gets; _lines[0] is number _next - _lines.Count.
        // Never reset (not even by Clear), so cursors handed out stay valid.
        private long _next;

        public RingBuffer(int maxLines, int trimAmount)
        {
            _maxLines = maxLines;
            _trimAmount = trimAmount;
            _lines = new List<LogLine>(maxLines);
        }

        public void Append(LogLine line)
        {
            lock (_lock)
            {
                _lines.Add(line);
                _next++;
                if (_lines.Count > _maxLines)
                {
                    _lines.RemoveRange(0, _trimAmount);
                }
            }
        }

        public IReadOnlyList<LogLine> Snapshot()
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }

        public LiveLogSlice Since(long since)
        {
            lock (_lock)
            {
                // A cursor ahead of this buffer came from before a DevDeck restart: start over.
                if (since > _next)
                {
                    return new LiveLogSlice(_lines.ToArray(), _next, Dropped: 0, Reset: true);
                }

                var first = _next - _lines.Count;
                var start = Math.Max(since, first);
                var lines = _lines.GetRange((int)(start - first), (int)(_next - start)).ToArray();
                var dropped = since > 0 ? Math.Max(0, first - since) : 0;
                return new LiveLogSlice(lines, _next, dropped, Reset: false);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }
    }
}

/// <param name="Lines">Lines after the requested cursor, oldest first.</param>
/// <param name="Next">Cursor to pass next time.</param>
/// <param name="Dropped">Lines after the cursor that were trimmed before they could be read.</param>
/// <param name="Reset">The cursor was not from this buffer (DevDeck restarted); the caller should
/// discard what it has and show <paramref name="Lines"/> from scratch.</param>
public sealed record LiveLogSlice(IReadOnlyList<LogLine> Lines, long Next, long Dropped, bool Reset);
