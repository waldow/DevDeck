namespace DevDeck.Web.Options;

public sealed class DevDeckOptions
{
    public const string SectionName = "DevDeck";

    public bool DevelopmentOnly { get; set; } = true;
    public bool AutoStartEnabledServices { get; set; } = false;
    public int StopTimeoutSeconds { get; set; } = 10;
    public int DashboardPollingMilliseconds { get; set; } = 1500;
    public int MaxLiveLogLinesPerService { get; set; } = 5000;
    public int LogTrimAmount { get; set; } = 1000;
    /// <summary>Run log files older than this are deleted by the retention sweep; 0 or negative keeps them forever.</summary>
    public int LogRetentionDays { get; set; } = 14;
    /// <summary>When true, DevDeck stops all managed services on shutdown instead of leaving them running as orphans.</summary>
    public bool StopServicesOnShutdown { get; set; } = false;
    public DevDeckReverseProxyOptions ReverseProxy { get; set; } = new();
    public AzuriteOptions Azurite { get; set; } = new();
}

public sealed class AzuriteOptions
{
    /// <summary>Executable used to launch Azurite; resolved against PATH (azurite.cmd on Windows).</summary>
    public string Command { get; set; } = "azurite";
    public int BlobPort { get; set; } = 10000;
    public int QueuePort { get; set; } = 10001;
    public int TablePort { get; set; } = 10002;
    /// <summary>How long to wait for Azurite's ports to come up before giving up.</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;
}

public sealed class DevDeckReverseProxyOptions
{
    public bool Enabled { get; set; } = true;
    public string GatewayBaseUrl { get; set; } = "http://localhost:5050";
    public bool AllowExternalDestinations { get; set; } = false;
    public bool AllowCatchAllRoutes { get; set; } = false;
    public bool EnableAutoStartOnRequest { get; set; } = false;
    public bool LogProxyRequests { get; set; } = true;
}
