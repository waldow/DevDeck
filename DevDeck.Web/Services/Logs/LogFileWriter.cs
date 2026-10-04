using System.Text;

namespace DevDeck.Web.Services.Logs;

public sealed class LogFileWriter : IAsyncDisposable
{
    // The global lock only guards the maps; each file's writes serialize on their own
    // entry so one chatty service's disk I/O doesn't stall every other writer (including
    // the proxy request path, which logs PRX lines through here).
    private readonly Dictionary<string, WriterEntry> _writers = new();

    // Paths closed for good (finished run logs), remembered so late output can't reopen them.
    // Bounded: only the most recent matter — a run's output readers are torn down right after
    // its log is closed, so stragglers arrive within moments, not after hundreds more runs.
    private const int MaxRememberedClosed = 512;
    private readonly HashSet<string> _closed = new();
    private readonly Queue<string> _closedOrder = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Append(string filePath, LogLine line)
    {
        // Appends arrive on process-output threadpool threads, where any escaping
        // exception is unhandled and kills the host. Serialize against Close/Dispose
        // (so we never write to a just-disposed writer) and swallow I/O failures —
        // the in-memory ring buffer still holds the line.
        WriterEntry? entry;
        try
        {
            lock (_lock)
            {
                // A finished run log stays closed: late output (e.g. from a grandchild still
                // holding the inherited pipe) must not reopen a handle nothing will release.
                if (_disposed || _closed.Contains(filePath)) return;
                if (!_writers.TryGetValue(filePath, out entry))
                {
                    entry = new WriterEntry(OpenWriter(filePath));
                    _writers[filePath] = entry;
                }
            }

            lock (entry)
            {
                if (entry.Disposed) return;
                entry.Writer.WriteLine(line.Format());
                entry.Writer.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // best-effort
        }
    }

    /// <summary>
    /// Flushes and releases the file. By default a later Append reopens it (Azurite reuses
    /// one log across relaunches); pass <paramref name="allowReopen"/> = false for a per-run
    /// log that is finished for good, so late output can't reopen a handle nothing releases.
    /// </summary>
    public void Close(string filePath, bool allowReopen = true)
    {
        WriterEntry? entry;
        lock (_lock)
        {
            if (!allowReopen) RememberClosed(filePath);
            if (!_writers.Remove(filePath, out entry)) return;
        }
        DisposeEntry(entry);
    }

    // Caller holds _lock.
    private void RememberClosed(string filePath)
    {
        if (!_closed.Add(filePath)) return;
        _closedOrder.Enqueue(filePath);
        while (_closedOrder.Count > MaxRememberedClosed)
        {
            _closed.Remove(_closedOrder.Dequeue());
        }
    }

    /// <summary>Whether a handle is currently open for <paramref name="filePath"/>.</summary>
    internal bool IsOpen(string filePath)
    {
        lock (_lock)
        {
            return _writers.ContainsKey(filePath);
        }
    }

    public ValueTask DisposeAsync()
    {
        WriterEntry[] entries;
        lock (_lock)
        {
            _disposed = true;
            entries = _writers.Values.ToArray();
            _writers.Clear();
        }
        foreach (var entry in entries)
        {
            DisposeEntry(entry);
        }
        return ValueTask.CompletedTask;
    }

    private static void DisposeEntry(WriterEntry entry)
    {
        lock (entry)
        {
            if (entry.Disposed) return;
            entry.Disposed = true;
            try
            {
                entry.Writer.Flush();
                entry.Writer.Dispose();
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static StreamWriter OpenWriter(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(false))
        {
            NewLine = "\n",
            AutoFlush = false,
        };
    }

    private sealed class WriterEntry(StreamWriter writer)
    {
        public StreamWriter Writer { get; } = writer;
        public bool Disposed { get; set; }
    }
}
