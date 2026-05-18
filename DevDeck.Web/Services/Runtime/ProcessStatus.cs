namespace DevDeck.Web.Services.Runtime;

public enum ProcessStatus
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Crashed = 5,
    FailedToStart = 6,
    Killed = 7,
}

public static class ProcessStatusNames
{
    public const string Starting = "Starting";
    public const string Running = "Running";
    public const string Stopping = "Stopping";
    public const string Stopped = "Stopped";
    public const string Crashed = "Crashed";
    public const string FailedToStart = "FailedToStart";
    public const string Killed = "Killed";
}
