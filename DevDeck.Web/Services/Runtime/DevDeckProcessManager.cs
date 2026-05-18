using System.Collections.Concurrent;
using System.Diagnostics;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Logs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Runtime;

public sealed class DevDeckProcessManager : IDevDeckProcessManager
{
    private readonly ConcurrentDictionary<int, RunningProcessInfo> _running = new();
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProcessLogBuffer _logBuffer;
    private readonly LogFileWriter _logFileWriter;
    private readonly CommandTemplateRenderer _renderer;
    private readonly CommandExecutableResolver _resolver;
    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly ILogger<DevDeckProcessManager> _logger;

    public DevDeckProcessManager(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProcessLogBuffer logBuffer,
        LogFileWriter logFileWriter,
        CommandTemplateRenderer renderer,
        CommandExecutableResolver resolver,
        IOptionsMonitor<DevDeckOptions> options,
        ILogger<DevDeckProcessManager> logger)
    {
        _dbFactory = dbFactory;
        _logBuffer = logBuffer;
        _logFileWriter = logFileWriter;
        _renderer = renderer;
        _resolver = resolver;
        _options = options;
        _logger = logger;
    }

    public RunningProcessInfo? GetRunningProcess(int serviceId) =>
        _running.TryGetValue(serviceId, out var info) ? info : null;

    public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => _running.Values.ToArray();

    public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => _logBuffer.Snapshot(serviceId);

    public void ClearLiveLogs(int serviceId) => _logBuffer.Clear(serviceId);

    public async Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken)
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

        var run = new ServiceRun
        {
            DevServiceId = service.Id,
            StartedUtc = DateTimeOffset.UtcNow,
            Status = ProcessStatusNames.Starting,
            StartCommandSnapshot = resolvedCommand,
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
            FileName = resolvedCommand,
            Arguments = argsRender.Text,
            WorkingDirectory = service.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        foreach (var env in service.EnvironmentVariables)
        {
            var rendered = _renderer.Render(env.Value, values).Text;
            psi.EnvironmentVariables[env.Key] = rendered;
        }

        Process process;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
        }
        catch (Exception ex)
        {
            run.Status = ProcessStatusNames.FailedToStart;
            run.StoppedUtc = DateTimeOffset.UtcNow;
            run.LastError = ex.Message;
            await db.SaveChangesAsync(cancellationToken);
            AppendSystemLine(service.Id, run.Id, logPath, $"Failed to start: {ex.Message}");
            return new StartServiceResult { ServiceId = serviceId, RunId = run.Id, Success = false, Error = ex.Message };
        }

        var info = new RunningProcessInfo
        {
            DevServiceId = service.Id,
            ServiceRunId = run.Id,
            ServiceName = service.Name,
            Process = process,
            StartedUtc = DateTimeOffset.UtcNow,
            LogFilePath = logPath,
            Port = service.Port,
            Url = service.Url,
            Status = ProcessStatus.Running,
        };
        _running[service.Id] = info;

        var serviceIdLocal = service.Id;
        var runIdLocal = run.Id;
        var logPathLocal = logPath;

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

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to begin stream read for service {ServiceId}", serviceIdLocal);
        }

        run.Status = ProcessStatusNames.Running;
        run.ProcessId = SafePid(process);
        await db.SaveChangesAsync(cancellationToken);

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

    public async Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!_running.TryGetValue(serviceId, out var info))
        {
            return new StopServiceResult { ServiceId = serviceId, Success = false, Error = "Service is not running." };
        }

        info.Status = ProcessStatus.Stopping;
        AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, "Stop requested");

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.StopTimeoutSeconds));
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            if (!info.Process.HasExited)
            {
                AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, "Killing process tree");
                info.Process.Kill(entireProcessTree: true);
            }

            while (!info.Process.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
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
            Success = info.Process.HasExited,
            Message = info.Process.HasExited ? "Stopped" : "Timed out waiting for exit",
        };
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

    private async Task HandleProcessExitedAsync(int serviceId, long runId, string logPath, Process process)
    {
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { /* ignore */ }
        AppendSystemLine(serviceId, runId, logPath, $"Process exited with code {exitCode?.ToString() ?? "?"}");
        _running.TryRemove(serviceId, out _);
        _logFileWriter.Close(logPath);

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var run = await db.ServiceRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is null) return;

            run.StoppedUtc = DateTimeOffset.UtcNow;
            run.ExitCode = exitCode;
            run.Status = run.Status switch
            {
                ProcessStatusNames.Stopping => ProcessStatusNames.Killed,
                _ => exitCode is 0 ? ProcessStatusNames.Stopped : ProcessStatusNames.Crashed,
            };
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
}
