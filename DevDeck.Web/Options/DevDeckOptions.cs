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
}

public sealed class DevDeckReverseProxyOptions
{
    public bool Enabled { get; set; } = true;
    public string GatewayBaseUrl { get; set; } = "http://localhost:5050";
    public bool AllowExternalDestinations { get; set; } = false;
    public bool AllowCatchAllRoutes { get; set; } = false;
    public bool EnableAutoStartOnRequest { get; set; } = false;
}
