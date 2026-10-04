namespace DevDeck.Web.Services.Runtime;

public sealed class StartServiceResult
{
    public bool Success { get; init; }
    public int ServiceId { get; init; }
    public long? RunId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
}

public sealed class StopServiceResult
{
    public bool Success { get; init; }
    public int ServiceId { get; init; }
    public long? RunId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    /// <summary>Nothing of the service was running (not a failure for Stop all).</summary>
    public bool NothingToStop { get; init; }
}

public sealed class RestartServiceResult
{
    public bool Success { get; init; }
    public int ServiceId { get; init; }
    public long? NewRunId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
}

public sealed class StartProfileResult
{
    public bool Success { get; init; }
    public int ProfileId { get; init; }
    public List<ServiceActionOutcome> Outcomes { get; init; } = [];
}

public sealed class StopAllResult
{
    public int Stopped { get; init; }
    public List<ServiceActionOutcome> Outcomes { get; init; } = [];
}

public sealed class StartAllResult
{
    public int Started { get; init; }
    public List<ServiceActionOutcome> Outcomes { get; init; } = [];
}

public sealed class ServiceActionOutcome
{
    public int ServiceId { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public bool Success { get; init; }
    /// <summary>Nothing needed doing (already running, passthru, disabled): not a failure.</summary>
    public bool Skipped { get; init; }
    public string? Message { get; init; }
}
