namespace DevDeck.Web.Data.Entities;

public sealed class DevService
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string ServiceType { get; set; }
    public required string WorkingDirectory { get; set; }
    public required string StartCommand { get; set; }
    public string? StartArguments { get; set; }
    public string? StopCommand { get; set; }
    public string? StopArguments { get; set; }
    public string? Url { get; set; }
    public int? Port { get; set; }
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }

    public List<ServiceEnvironmentVariable> EnvironmentVariables { get; set; } = [];
    public List<ServiceHealthCheck> HealthChecks { get; set; } = [];
    public List<ServiceRun> Runs { get; set; } = [];
    public List<ProxyRoute> ProxyRoutes { get; set; } = [];
}
