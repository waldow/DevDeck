using System.Collections.Concurrent;
using System.Text;

namespace DevDeck.Web.Services.Logs;

public sealed class LogFileWriter : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new();
    private readonly object _lock = new();

    public void Append(string filePath, LogLine line)
    {
        var writer = _writers.GetOrAdd(filePath, OpenWriter);
        lock (writer)
        {
            writer.WriteLine(line.Format());
            writer.Flush();
        }
    }

    public void Close(string filePath)
    {
        if (_writers.TryRemove(filePath, out var writer))
        {
            lock (writer)
            {
                try { writer.Flush(); writer.Dispose(); }
                catch { /* best-effort */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _writers)
        {
            try
            {
                await kvp.Value.FlushAsync();
                kvp.Value.Dispose();
            }
            catch
            {
                // best-effort
            }
        }
        _writers.Clear();
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
