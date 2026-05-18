namespace DevDeck.Web.Data.Entities;

public sealed class ServiceHealthCheck
{
    public int Id { get; set; }
    public int DevServiceId { get; set; }
    public DevService DevService { get; set; } = null!;
    public required string Url { get; set; }
    public int ExpectedStatusCode { get; set; } = 200;
    public int IntervalSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public string? LastStatus { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
}
