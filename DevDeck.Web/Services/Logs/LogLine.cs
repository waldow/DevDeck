namespace DevDeck.Web.Services.Logs;

public sealed class LogLine
{
    public required DateTimeOffset Timestamp { get; init; }
    public required int DevServiceId { get; init; }
    public required long ServiceRunId { get; init; }
    public required string Stream { get; init; }
    public required string Text { get; init; }

    public string Format() =>
        $"{Timestamp:O} [{Stream}] {Text}";
}
