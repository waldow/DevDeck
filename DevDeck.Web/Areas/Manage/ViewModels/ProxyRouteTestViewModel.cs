namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class ProxyRouteTestViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool RequireHealthyDestination { get; set; }
    public string MatchPath { get; set; } = string.Empty;
    public string? DestinationUrl { get; set; }
    public bool DestinationPortOpen { get; set; }
    public bool HasLinkedService { get; set; }
    public bool UseExternalInstance { get; set; }
    public int? ManagedPort { get; set; }
    public int? ExternalPort { get; set; }
    public int? EffectivePort { get; set; }
    public bool ServiceRunning { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public string ExampleProxyUrl { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}
