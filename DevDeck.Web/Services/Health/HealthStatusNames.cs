namespace DevDeck.Web.Services.Health;

public static class HealthStatusNames
{
    public const string Unknown = "Unknown";
    public const string Healthy = "Healthy";
    public const string Unhealthy = "Unhealthy";
    public const string Timeout = "Timeout";
    public const string NotRunning = "NotRunning";
    public const string Warming = "Warming";
}

public enum PortStatus
{
    Free = 0,
    UsedByDevDeckService = 1,
    UsedByOtherProcess = 2,
    Unknown = 3,
}
