namespace DevDeck.Web.Services.Portability;

// Portable JSON shape for import/export. No Ids, no timestamps, no runtime-only
// fields; foreign keys reference other entities by Name so documents are
// portable across machines.

public sealed class PortableServiceBundle
{
    public int SchemaVersion { get; set; } = PortabilityJson.CurrentSchemaVersion;
    public List<PortableService> Services { get; set; } = [];
}

public sealed class PortableService
{
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
    public bool AutoStart { get; set; }
    public bool UseExternalInstance { get; set; }
    public int? ExternalPort { get; set; }
    public int DisplayOrder { get; set; }
    public List<PortableEnvVar> EnvironmentVariables { get; set; } = [];
    public List<PortableHealthCheck> HealthChecks { get; set; } = [];
}

public sealed class PortableEnvVar
{
    public required string Key { get; set; }
    public string Value { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}

public sealed class PortableHealthCheck
{
    public required string Url { get; set; }
    public int ExpectedStatusCode { get; set; } = 200;
    public int IntervalSeconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
}

public sealed class PortableProfileBundle
{
    public int SchemaVersion { get; set; } = PortabilityJson.CurrentSchemaVersion;
    public List<PortableProfile> Profiles { get; set; } = [];
}

public sealed class PortableProfile
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public List<PortableProfileService> Services { get; set; } = [];
}

public sealed class PortableProfileService
{
    public required string ServiceName { get; set; }
    public int StartOrder { get; set; }
    public int StartDelaySeconds { get; set; }
}

public sealed class PortableRouteBundle
{
    public int SchemaVersion { get; set; } = PortabilityJson.CurrentSchemaVersion;
    public List<PortableRoute> Routes { get; set; } = [];
}

public sealed class PortableRoute
{
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public string? ServiceName { get; set; }
    public string? DestinationUrlOverride { get; set; }
    public required string MatchPath { get; set; }
    public string? MatchHostsCsv { get; set; }
    public int Order { get; set; }
    public string PathTransformMode { get; set; } = "None";
    public string? PathPrefixToRemove { get; set; }
    public string? PathPrefixToAdd { get; set; }
    public string? PathSet { get; set; }
    public bool PreserveHostHeader { get; set; }
    public bool AutoStartService { get; set; }
    public bool RequireHealthyDestination { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string? AuthorizationPolicy { get; set; }
    public bool ShowOnDashboard { get; set; } = true;
}
