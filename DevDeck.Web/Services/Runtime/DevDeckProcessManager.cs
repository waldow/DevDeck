using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Runtime;

public sealed class DevDeckProcessManager : IDevDeckProcessManager
{
    private readonly ConcurrentDictionary<int, RunningProcessInfo> _running = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _serviceLocks = new();
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProcessLogBuffer _logBuffer;
    private readonly LogFileWriter _logFileWriter;
    private readonly CommandTemplateRenderer _renderer;
    private readonly CommandExecutableResolver _resolver;
    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly HealthStatusCache _healthStatusCache;
    private readonly ILogger<DevDeckProcessManager> _logger;

    private static readonly TimeSpan PostStartHealthWarmup = TimeSpan.FromSeconds(15);

    public DevDeckProcessManager(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProcessLogBuffer logBuffer,
        LogFileWriter logFileWriter,
        CommandTemplateRenderer renderer,
        CommandExecutableResolver resolver,
        IOptionsMonitor<DevDeckOptions> options,
        IWebHostEnvironment environment,
        HealthStatusCache healthStatusCache,
        ILogger<DevDeckProcessManager> logger)
    {
        _dbFactory = dbFactory;
        _logBuffer = logBuffer;
        _logFileWriter = logFileWriter;
        _renderer = renderer;
        _resolver = resolver;
        _options = options;
        _environment = environment;
        _healthStatusCache = healthStatusCache;
        _logger = logger;
    }

    public RunningProcessInfo? GetRunningProcess(int serviceId) =>
        _running.TryGetValue(serviceId, out var info) ? info : null;

    public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => _running.Values.ToArray();

    public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => _logBuffer.Snapshot(serviceId);

    public void ClearLiveLogs(int serviceId) => _logBuffer.Clear(serviceId);

    public async Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!ExecutionAllowed())
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "DevDeck service execution is only enabled in Development." };
        }

        var serviceLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            if (_running.ContainsKey(serviceId))
            {
                return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service is already running." };
            }

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var service = await db.DevServices
                .Include(s => s.EnvironmentVariables)
                .FirstOrDefaultAsync(s => s.Id == serviceId, cancellationToken);

            if (service is null)
            {
                return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service not found." };
            }
            if (!service.Enabled)
            {
                return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service is disabled." };
            }
            if (!Directory.Exists(service.WorkingDirectory))
            {
                return new StartServiceResult { ServiceId = serviceId, Success = false, Error = $"Working directory does not exist: {service.WorkingDirectory}" };
            }

            var values = CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.Port, service.WorkingDirectory);
            var argsRender = _renderer.Render(service.StartArguments, values);
            var resolvedCommand = _resolver.Resolve(service.StartCommand);
            var renderedEnvironment = service.EnvironmentVariables
                .Select(env => new RenderedEnvironmentVariable(env.Key, _renderer.Render(env.Value, values).Text, env.IsSecret))
                .ToList();
            var launchCommand = _resolver.ResolveForLaunch(service.StartCommand, EffectivePathValue(renderedEnvironment));

            var run = new ServiceRun
            {
                DevServiceId = service.Id,
                StartedUtc = DateTimeOffset.UtcNow,
                Status = ProcessStatusNames.Starting,
                StartCommandSnapshot = launchCommand,
                StartArgumentsSnapshot = argsRender.Text,
                WorkingDirectorySnapshot = service.WorkingDirectory,
            };
            db.ServiceRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            var logPath = DevDeckPaths.LogFilePathFor(service.Name, run.Id, run.StartedUtc);
            run.LogFilePath = logPath;
            await db.SaveChangesAsync(cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = launchCommand,
                Arguments = argsRender.Text,
                WorkingDirectory = service.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };

            foreach (var env in renderedEnvironment)
            {
                psi.EnvironmentVariables[env.Key] = env.Value;
            }

            var serviceIdLocal = service.Id;
            var runIdLocal = run.Id;
            var logPathLocal = logPath;

            Process process;
            RunningProcessInfo info;
            try
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    WriteLine(serviceIdLocal, runIdLocal, logPathLocal, "OUT", e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    WriteLine(serviceIdLocal, runIdLocal, logPathLocal, "ERR", e.Data);
                };
                process.Exited += async (_, _) =>
                {
                    await HandleProcessExitedAsync(serviceIdLocal, runIdLocal, logPathLocal, process);
                };

                info = new RunningProcessInfo
                {
                    DevServiceId = service.Id,
                    ServiceRunId = run.Id,
                    ServiceName = service.Name,
                    Process = process,
                    StartedUtc = DateTimeOffset.UtcNow,
                    LogFilePath = logPath,
                    Port = service.Port,
                    Url = service.Url,
                    Status = ProcessStatus.Starting,
                };
                if (!_running.TryAdd(service.Id, info))
                {
                    return new StartServiceResult { ServiceId = service.Id, RunId = run.Id, Success = false, Error = "Service is already running." };
                }

                AppendLaunchDiagnostics(service.Id, run.Id, logPath, resolvedCommand, launchCommand, argsRender.Text,
                    service.WorkingDirectory, renderedEnvironment);
                process.Start();
            }
            catch (Exception ex)
            {
                _running.TryRemove(service.Id, out _);
                run.Status = ProcessStatusNames.FailedToStart;
                run.StoppedUtc = DateTimeOffset.UtcNow;
                run.LastError = ex.Message;
                await db.SaveChangesAsync(cancellationToken);
                AppendSystemLine(service.Id, run.Id, logPath, $"Failed to start: {ex.Message}");
                return new StartServiceResult { ServiceId = serviceId, RunId = run.Id, Success = false, Error = ex.Message };
            }

            info.Status = ProcessStatus.Running;
            run.Status = ProcessStatusNames.Running;
            run.ProcessId = SafePid(process);
            await db.SaveChangesAsync(cancellationToken);

            // Give health checks a grace window before they can fail proxy gates.
            _healthStatusCache.MarkStarting(service.Id, PostStartHealthWarmup);

            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to begin stream read for service {ServiceId}", serviceIdLocal);
            }

            AppendSystemLine(service.Id, run.Id, logPath, $"Process started with PID {run.ProcessId}");
            if (argsRender.UnknownPlaceholders.Count > 0)
            {
                AppendSystemLine(service.Id, run.Id, logPath,
                    $"Unresolved placeholders in arguments: {string.Join(", ", argsRender.UnknownPlaceholders)}");
            }

            return new StartServiceResult
            {
                ServiceId = service.Id,
                RunId = run.Id,
                Success = true,
                Message = $"Started PID {run.ProcessId}",
            };
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public async Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!ExecutionAllowed())
        {
            return new StopServiceResult { ServiceId = serviceId, Success = false, Error = "DevDeck service execution is only enabled in Development." };
        }

        var serviceLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            if (!_running.TryGetValue(serviceId, out var info))
            {
                return new StopServiceResult { ServiceId = serviceId, Success = false, Error = "Service is not running." };
            }

            info.Status = ProcessStatus.Stopping;
            AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, "Stop requested");
            await MarkRunStatusAsync(info.ServiceRunId, ProcessStatusNames.Stopping, cancellationToken);

            var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.StopTimeoutSeconds));
            var exited = false;
            try
            {
                if (!info.Process.HasExited)
                {
                    var signalled = TrySendGracefulShutdown(info.Process, serviceId, info.ServiceRunId, info.LogFilePath);
                    if (signalled)
                    {
                        exited = await WaitForExitAsync(info.Process, timeout, cancellationToken);
                    }
                }
                else
                {
                    exited = true;
                }

                if (!exited && !info.Process.HasExited)
                {
                    info.KillIssued = true;
                    AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, "Killing process tree");
                    info.Process.Kill(entireProcessTree: true);
                    exited = await WaitForExitAsync(info.Process, TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, $"Error stopping process: {ex.Message}");
            }

            return new StopServiceResult
            {
                ServiceId = serviceId,
                RunId = info.ServiceRunId,
                Success = exited || info.Process.HasExited,
                Message = exited || info.Process.HasExited ? "Stopped" : "Timed out waiting for exit",
            };
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public async Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (_running.ContainsKey(serviceId))
        {
            await StopServiceAsync(serviceId, cancellationToken);
            await Task.Delay(250, cancellationToken);
        }
        var start = await StartServiceAsync(serviceId, cancellationToken);
        return new RestartServiceResult
        {
            ServiceId = serviceId,
            NewRunId = start.RunId,
            Success = start.Success,
            Message = start.Message,
            Error = start.Error,
        };
    }

    public async Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var profile = await db.LaunchProfiles
            .Include(p => p.Services)
            .ThenInclude(s => s.DevService)
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

        if (profile is null)
        {
            return new StartProfileResult { ProfileId = profileId, Success = false };
        }

        var outcomes = new List<ServiceActionOutcome>();
        var ordered = profile.Services.OrderBy(s => s.StartOrder).ToList();
        foreach (var entry in ordered)
        {
            var result = await StartServiceAsync(entry.DevServiceId, cancellationToken);
            outcomes.Add(new ServiceActionOutcome
            {
                ServiceId = entry.DevServiceId,
                ServiceName = entry.DevService.Name,
                Success = result.Success,
                Message = result.Message ?? result.Error,
            });
            if (entry.StartDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(entry.StartDelaySeconds), cancellationToken);
            }
        }
        return new StartProfileResult { ProfileId = profileId, Success = outcomes.All(o => o.Success), Outcomes = outcomes };
    }

    public async Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken)
    {
        List<(int Id, string Name)> targets;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            targets = await db.DevServices
                .Where(s => s.Enabled)
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
                .Select(s => new ValueTuple<int, string>(s.Id, s.Name))
                .ToListAsync(cancellationToken);
        }

        var outcomes = new List<ServiceActionOutcome>();
        foreach (var (id, name) in targets)
        {
            if (_running.ContainsKey(id)) continue; // already running — skip
            var result = await StartServiceAsync(id, cancellationToken);
            outcomes.Add(new ServiceActionOutcome
            {
                ServiceId = id,
                ServiceName = name,
                Success = result.Success,
                Message = result.Message ?? result.Error,
            });
        }
        return new StartAllResult { Started = outcomes.Count(o => o.Success), Outcomes = outcomes };
    }

    public async Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken)
    {
        var ids = _running.Keys.ToArray();
        var outcomes = new List<ServiceActionOutcome>();
        foreach (var id in ids)
        {
            var info = _running.TryGetValue(id, out var existing) ? existing : null;
            var result = await StopServiceAsync(id, cancellationToken);
            outcomes.Add(new ServiceActionOutcome
            {
                ServiceId = id,
                ServiceName = info?.ServiceName ?? id.ToString(),
                Success = result.Success,
                Message = result.Message ?? result.Error,
            });
        }
        return new StopAllResult { Stopped = outcomes.Count(o => o.Success), Outcomes = outcomes };
    }

    private void WriteLine(int serviceId, long runId, string logPath, string stream, string text)
    {
        var line = new LogLine
        {
            Timestamp = DateTimeOffset.UtcNow,
            DevServiceId = serviceId,
            ServiceRunId = runId,
            Stream = stream,
            Text = text,
        };
        _logBuffer.Append(serviceId, line);
        _logFileWriter.Append(logPath, line);
    }

    private void AppendSystemLine(int serviceId, long runId, string logPath, string text) =>
        WriteLine(serviceId, runId, logPath, "SYS", text);

    private void AppendLaunchDiagnostics(
        int serviceId,
        long runId,
        string logPath,
        string resolvedCommand,
        string launchCommand,
        string arguments,
        string workingDirectory,
        IReadOnlyCollection<RenderedEnvironmentVariable> environment)
    {
        AppendSystemLine(serviceId, runId, logPath, $"Launch command: {QuoteForLog(launchCommand)} {arguments}".TrimEnd());
        AppendSystemLine(serviceId, runId, logPath, $"Working directory: {workingDirectory}");
        if (!string.Equals(resolvedCommand, launchCommand, StringComparison.OrdinalIgnoreCase))
        {
            AppendSystemLine(serviceId, runId, logPath, $"Resolved executable: {resolvedCommand} -> {launchCommand}");
        }
        AppendSystemLine(serviceId, runId, logPath, $"Environment overrides: {FormatEnvironmentOverrides(environment)}");
    }

    private async Task HandleProcessExitedAsync(int serviceId, long runId, string logPath, Process process)
    {
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { /* ignore */ }
        AppendSystemLine(serviceId, runId, logPath, $"Process exited with code {exitCode?.ToString() ?? "?"}");
        _running.TryRemove(serviceId, out var info);
        _healthStatusCache.RemoveService(serviceId);
        _logFileWriter.Close(logPath);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var run = await db.ServiceRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is null) return;

            run.StoppedUtc = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            run.Status = ProcessStatusNames.ResolveExitStatus(run.Status, exitCode, info?.KillIssued == true);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ServiceRun {RunId} on exit", runId);
        }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    private static string? EffectivePathValue(IReadOnlyCollection<RenderedEnvironmentVariable> environment)
    {
        var pathOverride = environment.LastOrDefault(e => string.Equals(e.Key, "PATH", StringComparison.OrdinalIgnoreCase));
        return pathOverride?.Value ?? Environment.GetEnvironmentVariable("PATH");
    }

    private static string FormatEnvironmentOverrides(IReadOnlyCollection<RenderedEnvironmentVariable> environment)
    {
        if (environment.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", environment.Select(e => $"{e.Key}={(e.IsSecret ? "***" : TrimForLog(e.Value))}"));
    }

    private static string TrimForLog(string value)
    {
        const int maxLength = 200;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string QuoteForLog(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    private bool ExecutionAllowed() =>
        !_options.CurrentValue.DevelopmentOnly || _environment.IsDevelopment();

    private async Task MarkRunStatusAsync(long runId, string status, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var run = await db.ServiceRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (run is null) return;
            run.Status = status;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark ServiceRun {RunId} as {Status}", runId, status);
        }
    }

    private bool TrySendGracefulShutdown(Process process, int serviceId, long runId, string logPath)
    {
        try
        {
            if (process.HasExited) return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // CloseMainWindow only reaches processes that have a main window; console
                // children won't react but the kill fallback handles them.
                var closed = process.CloseMainWindow();
                AppendSystemLine(serviceId, runId, logPath,
                    closed ? "Sent close to main window" : "No main window; awaiting kill fallback");
                return true;
            }

            // Unix: send SIGTERM via libc kill — Process.Kill() is SIGKILL on .NET so we
            // PInvoke directly to let the child shut down gracefully (npm/dotnet/func all
            // listen for SIGTERM).
            var pid = SafePid(process);
            if (pid is null) return false;

            var rc = PosixKill(pid.Value, SIGTERM);
            if (rc != 0)
            {
                AppendSystemLine(serviceId, runId, logPath, $"kill(SIGTERM) failed with errno {Marshal.GetLastWin32Error()}");
                return false;
            }
            AppendSystemLine(serviceId, runId, logPath, "Sent SIGTERM");
            return true;
        }
        catch (Exception ex)
        {
            AppendSystemLine(serviceId, runId, logPath, $"Graceful shutdown signal failed: {ex.Message}");
            return false;
        }
    }

    private const int SIGTERM = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int PosixKill(int pid, int sig);

    private sealed record RenderedEnvironmentVariable(string Key, string Value, bool IsSecret);

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
