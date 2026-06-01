using System.ComponentModel.DataAnnotations.Schema;

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

    /// <summary>
    /// Passthru mode: DevDeck does not launch or manage this service's process; instead it
    /// proxies to, health-checks, and reports the status of an externally-running instance
    /// (e.g. one started by Visual Studio's debugger) on <see cref="ExternalPort"/>.
    /// </summary>
    public bool UseExternalInstance { get; set; } = false;

    /// <summary>Port of the externally-running instance to passthru to (defaults to the
    /// service's dev port, e.g. 7071 for Azure Functions). Only used when
    /// <see cref="UseExternalInstance"/> is set.</summary>
    public int? ExternalPort { get; set; }

    public int DisplayOrder { get; set; } = 0;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }

    /// <summary>The port the service actually listens on from DevDeck's point of view: the
    /// external instance's port in passthru mode, otherwise the configured run port. Used
    /// when rendering proxy destinations, health-check URLs, and status probes.</summary>
    [NotMapped]
    public int? EffectivePort => UseExternalInstance ? (ExternalPort ?? Port) : Port;

    public List<ServiceEnvironmentVariable> EnvironmentVariables { get; set; } = [];
    public List<ServiceHealthCheck> HealthChecks { get; set; } = [];
    public List<ServiceRun> Runs { get; set; } = [];
    public List<ProxyRoute> ProxyRoutes { get; set; } = [];
}
