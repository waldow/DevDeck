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

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }
    }
}
