using DevDeck.Web.Services.Logs;

namespace DevDeck.Web.Services.Runtime;

public interface IDevDeckProcessManager
{
    Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken);
    Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken);
    Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken);
    Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken);
    Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken);
    Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken);
    RunningProcessInfo? GetRunningProcess(int serviceId);
    IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses();
    IReadOnlyList<LogLine> GetLiveLogs(int serviceId);
    void ClearLiveLogs(int serviceId);
    void AppendProxyLog(int serviceId, string text);
}
