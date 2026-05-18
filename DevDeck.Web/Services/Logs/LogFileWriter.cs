using System.Collections.Concurrent;

namespace DevDeck.Web.Services.Logs;

public sealed class LogFileWriter
{
    private readonly ConcurrentDictionary<string, object> _locks = new();
    private readonly ILogger<LogFileWriter> _logger;

    public LogFileWriter(ILogger<LogFileWriter> logger)
    {
        _logger = logger;
    }

    public void Append(string logPath, LogLine line)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileLock = _locks.GetOrAdd(logPath, _ => new object());
            lock (fileLock)
            {
                File.AppendAllText(logPath, line.Format() + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to append process log line to {LogPath}", logPath);
        }
    }

    public void Close(string logPath)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            _locks.TryRemove(logPath, out _);
        }
    }
}
