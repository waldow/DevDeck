using DevDeck.Web.Data.Entities;

namespace DevDeck.Web.Areas.Manage.ViewModels;

public sealed class DashboardViewModel
{
    public List<DashboardCard> Cards { get; set; } = new();
    public int PollingMilliseconds { get; set; } = 1500;
}

public sealed class DashboardCard
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServiceType { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string RuntimeStatus { get; set; } = "Stopped";
    public string HealthStatus { get; set; } = "Unknown";
    public int? Port { get; set; }
    public string? Url { get; set; }
    public string? ProxyUrl { get; set; }
    public int? ProcessId { get; set; }
    public long? RunId { get; set; }
}
