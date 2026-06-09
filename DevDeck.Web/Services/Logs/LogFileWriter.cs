using System.Text;

namespace DevDeck.Web.Services.Logs;

public sealed class LogFileWriter : IAsyncDisposable
{
    private readonly Dictionary<string, StreamWriter> _writers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Append(string filePath, LogLine line)
    {
        // Appends arrive on process-output threadpool threads, where any escaping
        // exception is unhandled and kills the host. Serialize against Close/Dispose
        // (so we never write to a just-disposed writer) and swallow I/O failures —
        // the in-memory ring buffer still holds the line.
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                if (!_writers.TryGetValue(filePath, out var writer))
                {
                    writer = OpenWriter(filePath);
                    _writers[filePath] = writer;
                }
                writer.WriteLine(line.Format());
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // best-effort
            }
        }
    }

    public void Close(string filePath)
    {
        lock (_lock)
        {
            if (_writers.Remove(filePath, out var writer))
            {
                try { writer.Flush(); writer.Dispose(); }
                catch { /* best-effort */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var writer in _writers.Values)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                catch
                {
                    // best-effort
                }
            }
            _writers.Clear();
        }
        return ValueTask.CompletedTask;
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
}
