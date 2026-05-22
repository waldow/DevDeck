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
    public int LogRetentionDays { get; set; } = 14;
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
}
