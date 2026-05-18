using System.Globalization;

namespace DevDeck.Web.Services.Logs;

public sealed class LogLine
{
    public DateTimeOffset Timestamp { get; init; }

    public int DevServiceId { get; init; }

    public long ServiceRunId { get; init; }

    public required string Stream { get; init; }

    public required string Text { get; init; }

    public string Format()
    {
        var timestamp = Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        return $"{timestamp} [{Stream}] {Text}";
    }
}
