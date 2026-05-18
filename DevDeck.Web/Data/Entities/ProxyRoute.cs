namespace DevDeck.Web.Data.Entities;

public sealed class ProxyRoute
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public int? DevServiceId { get; set; }
    public DevService? DevService { get; set; }
    public string? DestinationUrlOverride { get; set; }
    public required string MatchPath { get; set; }
    public string? MatchHostsCsv { get; set; }
    public int Order { get; set; } = 0;
    public required string PathTransformMode { get; set; } = "None";
    public string? PathPrefixToRemove { get; set; }
    public string? PathPrefixToAdd { get; set; }
    public string? PathSet { get; set; }
    public bool PreserveHostHeader { get; set; } = false;
    public bool AutoStartService { get; set; } = false;
    public bool RequireHealthyDestination { get; set; } = false;
    public int? TimeoutSeconds { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public bool ShowOnDashboard { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }
}
