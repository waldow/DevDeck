using System.Collections.Concurrent;
using System.Diagnostics;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Runtime;

public sealed class DevDeckProcessManager : IDevDeckProcessManager, IDisposable
{
    private readonly ConcurrentDictionary<int, RunningProcessInfo> _running = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _serviceLocks = new();
    private volatile bool _shuttingDown;
    private readonly IDbContextFactory<DevDeckDbContext> _dbFactory;
    private readonly ProcessLogBuffer _logBuffer;
    private readonly LogFileWriter _logFileWriter;
    private readonly CommandTemplateRenderer _renderer;
    private readonly CommandExecutableResolver _resolver;
    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly HealthStatusCache _healthStatusCache;
    private readonly IAzuriteSupervisor _azuriteSupervisor;
    private readonly ILogger<DevDeckProcessManager> _logger;

    private const string AzureFunctionServiceType = "AzureFunction";
    private const string AzureFunctionsServiceType = "AzureFunctions";
    private const string AzureWebJobsStorageKey = "AzureWebJobsStorage";
    private const string AzureWebJobsStorageDevelopmentValue = "UseDevelopmentStorage=true";

    private static readonly TimeSpan PostStartHealthWarmup = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinimumStopGrace = TimeSpan.FromSeconds(2);
    private static readonly string[] ActiveStatuses =
    [
        ProcessStatusNames.Starting,
        ProcessStatusNames.Running,
        ProcessStatusNames.Stopping,
    ];

    // DevDeck's own hosting settings (e.g. the ASPNETCORE_URLS `dotnet run` sets from its launch
    // profile). Inherited by an ASP.NET Core service, they would bind it to DevDeck's URL instead
    // of its own port — so a service only gets them when it sets them itself.
    private static readonly string[] DevDeckHostingVariables =
    [
        "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
        "DOTNET_URLS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS",
    ];

    public DevDeckProcessManager(
        IDbContextFactory<DevDeckDbContext> dbFactory,
        ProcessLogBuffer logBuffer,
        LogFileWriter logFileWriter,
        CommandTemplateRenderer renderer,
        CommandExecutableResolver resolver,
        IOptionsMonitor<DevDeckOptions> options,
        IWebHostEnvironment environment,
        HealthStatusCache healthStatusCache,
        IAzuriteSupervisor azuriteSupervisor,
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
        _azuriteSupervisor = azuriteSupervisor;
        _logger = logger;
    }

    public RunningProcessInfo? GetRunningProcess(int serviceId) =>
        _running.TryGetValue(serviceId, out var info) ? info : null;

    public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() => _running.Values.ToArray();

    public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => _logBuffer.Snapshot(serviceId);

    public LiveLogSlice GetLiveLogsSince(int serviceId, long since) => _logBuffer.SnapshotSince(serviceId, since);

    // True while a start, stop or restart holds the service's lock, i.e. that operation owns
    // the service's run rows until it finishes.
    public bool IsServiceBusy(int serviceId) =>
        _serviceLocks.TryGetValue(serviceId, out var serviceLock) && serviceLock.CurrentCount == 0;

    public void ClearLiveLogs(int serviceId) => _logBuffer.Clear(serviceId);

    /// <summary>
    /// Host shutdown has begun: refuse every start from now on (auto-start, a request still in
    /// flight), including one already underway but not yet launched, so nothing is launched
    /// after — and missed by — the shutdown Stop-all.
    /// </summary>
    public void BeginShutdown() => _shuttingDown = true;

    private const string ShuttingDownError = "DevDeck is shutting down.";

    // Lifecycle operations (start, stop, restart and the batch forms of them) run to completion
    // once begun: the caller's token is not honoured, because an HTTP request aborted halfway
    // (the user navigating away to watch the logs) must not leave a run half-started, a stop
    // half-done, or a restart turned into a stop. Every wait inside is bounded by a timeout.
    public async Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!ExecutionAllowed())
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "DevDeck service execution is only enabled in Development." };
        }

        var serviceLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(CancellationToken.None);
        try
        {
            return await StartServiceCoreAsync(serviceId);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    // Callers must hold the per-service lock.
    private async Task<StartServiceResult> StartServiceCoreAsync(int serviceId)
    {
        if (_shuttingDown)
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = ShuttingDownError };
        }
        if (_running.ContainsKey(serviceId))
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service is already running." };
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var service = await db.DevServices
            .Include(s => s.EnvironmentVariables)
            .FirstOrDefaultAsync(s => s.Id == serviceId);

        if (service is null)
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service not found." };
        }
        if (!service.Enabled)
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = "Service is disabled." };
        }
        if (service.UseExternalInstance)
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = $"'{service.Name}' uses an external instance on port {service.EffectivePort}; DevDeck does not launch it." };
        }

        // An instance an earlier DevDeck session launched may still be running: that is this
        // service, running — take it back under management instead of launching a duplicate
        // that would fight it for its port.
        if (await TryAdoptLiveRunAsync(db, service) is { } adopted)
        {
            return new StartServiceResult
            {
                ServiceId = serviceId,
                RunId = adopted.ServiceRunId,
                Success = false,
                Error = $"Service is already running (PID {SafePid(adopted.Process)}, left running by an earlier DevDeck session).",
            };
        }

        if (!Directory.Exists(service.WorkingDirectory))
        {
            return new StartServiceResult { ServiceId = serviceId, Success = false, Error = $"Working directory does not exist: {service.WorkingDirectory}" };
        }

        var values = CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.Port, service.WorkingDirectory);
        var argsRender = _renderer.RenderArguments(service.StartArguments, values);
        var resolvedCommand = _resolver.Resolve(service.StartCommand);
        var renderedEnvironment = RenderEnvironment(service, values);
        var isAzureFunction = IsAzureFunctionServiceType(service.ServiceType);
        AddAzureFunctionsDefaults(renderedEnvironment, isAzureFunction);
        var launchCommand = _resolver.ResolveForLaunch(service.StartCommand, EffectivePathValue(renderedEnvironment), service.WorkingDirectory);

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
        await db.SaveChangesAsync();

        var logPath = DevDeckPaths.LogFilePathFor(service.Name, run.Id, run.StartedUtc);
        run.LogFilePath = logPath;
        await db.SaveChangesAsync();

        // Azure Functions need AzureWebJobsStorage (Azurite locally) and fail on startup
        // without it — ensure the emulator is healthy before launching the Functions host.
        if (isAzureFunction)
        {
            AzuriteReadyResult azurite;
            try
            {
                AppendSystemLine(service.Id, run.Id, logPath, "Ensuring Azurite storage emulator is running...");
                azurite = await _azuriteSupervisor.EnsureRunningAsync(
                    msg => AppendSystemLine(service.Id, run.Id, logPath, msg), CancellationToken.None);
            }
            catch (Exception ex)
            {
                azurite = new AzuriteReadyResult(false, ex.Message);
            }

            if (!azurite.Success)
            {
                return await FailRunAsync(db, run, logPath, $"Azurite not ready: {azurite.Error}", azurite.Error ?? "Azurite not ready.");
            }
        }

        // Shutdown may have begun while Azurite was coming up.
        if (_shuttingDown)
        {
            return await FailRunAsync(db, run, logPath, $"Not launched: {ShuttingDownError}", ShuttingDownError);
        }

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

        foreach (var name in DevDeckHostingVariables)
        {
            if (!renderedEnvironment.Any(e => string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)))
            {
                psi.Environment.Remove(name);
            }
        }
        foreach (var env in renderedEnvironment)
        {
            psi.Environment[env.Key] = env.Value;
        }
        var ownSession = ProcessSessions.StartInNewSession(psi);

        var serviceIdLocal = service.Id;
        var runIdLocal = run.Id;
        var logPathLocal = logPath;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // Exited can fire before this method has attached the output readers and recorded the
        // run as Running (a command that fails instantly). Finalizing at that point would
        // dispose the Process under us, lose all of the run's output, and let the Running save
        // below overwrite the final status — so the exit handler first waits for this gate,
        // which opens once the start sequence has finished (or failed).
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunningProcessInfo? info = null;
        try
        {
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
                // Whether a stop was underway when the process exited, read at the exit itself:
                // a Stop that arrives just after a crash must not relabel the crash as Stopped.
                var stopRequested = info?.Status == ProcessStatus.Stopping;
                // async void event handler: an escaping exception is unhandled and
                // would take down the whole host, so nothing may run outside this try.
                try
                {
                    await startGate.Task;
                    await HandleProcessExitedAsync(serviceIdLocal, runIdLocal, logPathLocal, process, info, stopRequested);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to finalize exited service {ServiceId}", serviceIdLocal);
                }
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
                process.Dispose();
                startGate.TrySetResult();
                return await FailRunAsync(db, run, logPath, "Service is already running.", "Service is already running.");
            }

            AppendLaunchDiagnostics(service.Id, run.Id, logPath, resolvedCommand, launchCommand, argsRender.Text,
                service.WorkingDirectory, renderedEnvironment);
            process.Start();
            if (ownSession)
            {
                // setsid made the process the leader of a new group whose id is its PID.
                info.ProcessGroup = SafePid(process);
            }
        }
        catch (Exception ex)
        {
            if (info is not null)
            {
                _running.TryRemove(new KeyValuePair<int, RunningProcessInfo>(service.Id, info));
            }
            process.Dispose();
            startGate.TrySetResult();
            return await FailRunAsync(db, run, logPath, $"Failed to start: {ex.Message}", ex.Message);
        }

        // The process is live from here on, so nothing below may bail out early: a tracked
        // process whose output pipes are never drained would block once the pipe buffer fills.
        // Failures are only logged.
        try
        {
            info.Status = ProcessStatus.Running;
            run.Status = ProcessStatusNames.Running;
            run.ProcessId = SafePid(process);
            run.ProcessStartKey = run.ProcessId is int startedPid && ProcessTree.TryGetIdentity(startedPid, out var identity)
                ? identity.StartKey
                : null;
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record ServiceRun {RunId} as running", run.Id);
            }

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

            AppendSystemLine(service.Id, run.Id, logPath, $"Process started with PID {run.ProcessId}" +
                (ownSession ? " in its own process group" : string.Empty));
            if (argsRender.UnknownPlaceholders.Count > 0)
            {
                AppendSystemLine(service.Id, run.Id, logPath,
                    $"Unresolved placeholders in arguments: {string.Join(", ", argsRender.UnknownPlaceholders)}");
            }
        }
        finally
        {
            startGate.TrySetResult();
        }

        return new StartServiceResult
        {
            ServiceId = service.Id,
            RunId = run.Id,
            Success = true,
            Message = $"Started PID {run.ProcessId}",
        };
    }

    // Records a start that failed before (or while) launching the process, and releases the
    // run's log file: no exit handler will ever run for it.
    private async Task<StartServiceResult> FailRunAsync(DevDeckDbContext db, ServiceRun run, string logPath, string logLine, string error)
    {
        run.Status = ProcessStatusNames.FailedToStart;
        run.StoppedUtc = DateTimeOffset.UtcNow;
        run.LastError = error;
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record ServiceRun {RunId} as failed to start", run.Id);
        }
        AppendSystemLine(run.DevServiceId, run.Id, logPath, logLine);
        _logFileWriter.Close(logPath, allowReopen: false);
        return new StartServiceResult { ServiceId = run.DevServiceId, RunId = run.Id, Success = false, Error = error };
    }

    public async Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!ExecutionAllowed())
        {
            return new StopServiceResult { ServiceId = serviceId, Success = false, Error = "DevDeck service execution is only enabled in Development." };
        }

        var serviceLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(CancellationToken.None);
        try
        {
            return await StopServiceCoreAsync(serviceId);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    // Callers must hold the per-service lock. Stops the tracked process, then anything an
    // earlier DevDeck session left running for the service.
    private async Task<StopServiceResult> StopServiceCoreAsync(int serviceId)
    {
        StopServiceResult? tracked = null;
        if (_running.TryGetValue(serviceId, out var info))
        {
            tracked = await StopTrackedAsync(info);
        }

        var orphans = await StopOrphanedServiceRunsAsync(serviceId, excludeRunId: info?.ServiceRunId);
        if (tracked is null)
        {
            return orphans;
        }
        if (orphans.NothingToStop)
        {
            return tracked;
        }

        return new StopServiceResult
        {
            ServiceId = serviceId,
            RunId = tracked.RunId,
            Success = tracked.Success && orphans.Success,
            Message = $"{tracked.Message ?? tracked.Error}; {orphans.Message ?? orphans.Error}",
        };
    }

    // The Exited handler disposes the Process object concurrently, so every touch goes
    // through the Safe* helpers (inside ProcessTerminator too).
    private async Task<StopServiceResult> StopTrackedAsync(RunningProcessInfo info)
    {
        var serviceId = info.DevServiceId;
        info.Status = ProcessStatus.Stopping;
        AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, "Stop requested");
        await MarkRunStoppingAsync(info.ServiceRunId);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.StopTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var outcome = new StopOutcome(false, false, 0);
        try
        {
            await RunStopCommandAsync(info, timeout);
            var grace = deadline - DateTimeOffset.UtcNow;
            outcome = await ProcessTerminator.StopTreeAsync(
                info.Process,
                info.ProcessGroup,
                grace > MinimumStopGrace ? grace : MinimumStopGrace,
                line => AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, line),
                () => info.KillIssued = true);
        }
        catch (Exception ex)
        {
            AppendSystemLine(serviceId, info.ServiceRunId, info.LogFilePath, $"Error stopping process: {ex.Message}");
        }

        var stopped = outcome.Exited || SafeHasExited(info.Process);
        if (stopped)
        {
            // The Exited handler finalizes the run and then clears the running map; wait
            // for it so callers (Restart, shutdown Stop-all) observe a finished run rather
            // than racing a still-pending DB update or a stale "already running".
            await WaitForRunningRemovalAsync(serviceId, TimeSpan.FromSeconds(5));
        }
        return new StopServiceResult
        {
            ServiceId = serviceId,
            RunId = info.ServiceRunId,
            Success = stopped,
            Message = !stopped
                ? "Timed out waiting for exit"
                : outcome.LeftoversKilled > 0
                    ? $"Stopped (killed {outcome.LeftoversKilled} process(es) it left behind)"
                    : "Stopped",
        };
    }

    /// <summary>
    /// Runs the service's configured stop command (e.g. <c>docker compose stop</c>) in its
    /// working directory, before the process is signalled, for tools whose clean shutdown needs
    /// more than a signal. Its output goes to the service's log.
    /// </summary>
    private async Task RunStopCommandAsync(RunningProcessInfo info, TimeSpan timeout)
    {
        DevService? service;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            service = await db.DevServices
                .Include(s => s.EnvironmentVariables)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == info.DevServiceId);
        }
        if (service is null || string.IsNullOrWhiteSpace(service.StopCommand) || !Directory.Exists(service.WorkingDirectory))
        {
            return;
        }

        void Log(string stream, string text) => WriteLine(info.DevServiceId, info.ServiceRunId, info.LogFilePath, stream, text);

        var values = CommandTemplateRenderer.BuildValues(service.Id, service.Name, service.Port, service.WorkingDirectory);
        var environment = RenderEnvironment(service, values);
        AddAzureFunctionsDefaults(environment, IsAzureFunctionServiceType(service.ServiceType));
        var command = _resolver.ResolveForLaunch(service.StopCommand, EffectivePathValue(environment), service.WorkingDirectory);
        var arguments = _renderer.RenderArguments(service.StopArguments, values).Text;

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = service.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var env in environment)
        {
            psi.Environment[env.Key] = env.Value;
        }

        Log("SYS", $"Running stop command: {QuoteForLog(command)} {arguments}".TrimEnd());
        try
        {
            using var stopProcess = new Process { StartInfo = psi };
            stopProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) Log("OUT", e.Data); };
            stopProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log("ERR", e.Data); };
            stopProcess.Start();
            stopProcess.BeginOutputReadLine();
            stopProcess.BeginErrorReadLine();
            if (await WaitForExitAsync(stopProcess, timeout))
            {
                Log("SYS", $"Stop command exited with code {TryGetExitCode(stopProcess)?.ToString() ?? "?"}");
            }
            else
            {
                Log("SYS", $"Stop command did not finish within {timeout.TotalSeconds:0}s; killing it");
                try { stopProcess.Kill(entireProcessTree: true); } catch { /* exited meanwhile */ }
            }
        }
        catch (Exception ex)
        {
            Log("SYS", $"Stop command failed: {ex.Message}");
        }
    }

    public async Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken)
    {
        if (!ExecutionAllowed())
        {
            return new RestartServiceResult { ServiceId = serviceId, Success = false, Error = "DevDeck service execution is only enabled in Development." };
        }

        // Hold the per-service lock across the whole stop→start sequence so a concurrent
        // start/stop can't interleave between the two halves of the restart.
        var serviceLock = _serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(CancellationToken.None);
        try
        {
            // Also covers an instance left running by an earlier DevDeck session.
            await StopServiceCoreAsync(serviceId);
            var start = await StartServiceCoreAsync(serviceId);
            return new RestartServiceResult
            {
                ServiceId = serviceId,
                NewRunId = start.RunId,
                Success = start.Success,
                Message = start.Message,
                Error = start.Error,
            };
        }
        finally
        {
            serviceLock.Release();
        }
    }

    private async Task WaitForRunningRemovalAsync(int serviceId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_running.ContainsKey(serviceId) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    public async Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken)
    {
        LaunchProfile? profile;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            profile = await db.LaunchProfiles
                .Include(p => p.Services)
                .ThenInclude(s => s.DevService)
                .FirstOrDefaultAsync(p => p.Id == profileId);
        }

        if (profile is null)
        {
            return new StartProfileResult { ProfileId = profileId, Success = false };
        }

        var outcomes = new List<ServiceActionOutcome>();
        var ordered = profile.Services.OrderBy(s => s.StartOrder).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var service = entry.DevService;

            // "Start each service if not already running": members that need nothing (or that
            // DevDeck never launches) are reported as skipped, not as failures, and cost no delay.
            var skipReason = !service.Enabled ? "disabled"
                : service.UseExternalInstance ? "passthru"
                : _running.ContainsKey(service.Id) ? "already running"
                : null;
            if (skipReason is not null)
            {
                outcomes.Add(new ServiceActionOutcome
                {
                    ServiceId = service.Id, ServiceName = service.Name, Success = true, Skipped = true, Message = skipReason,
                });
                continue;
            }

            var result = await StartServiceAsync(entry.DevServiceId, CancellationToken.None);
            outcomes.Add(new ServiceActionOutcome
            {
                ServiceId = entry.DevServiceId,
                ServiceName = service.Name,
                Success = result.Success,
                Message = result.Message ?? result.Error,
            });
            if (result.Success && entry.StartDelaySeconds > 0 && i < ordered.Count - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(entry.StartDelaySeconds));
            }
        }
        return new StartProfileResult { ProfileId = profileId, Success = outcomes.All(o => o.Success), Outcomes = outcomes };
    }

    public async Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken)
    {
        List<(int Id, string Name)> targets;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            targets = await db.DevServices
                .Where(s => s.Enabled && !s.UseExternalInstance)
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name)
                .Select(s => new ValueTuple<int, string>(s.Id, s.Name))
                .ToListAsync();
        }

        var outcomes = new List<ServiceActionOutcome>();
        foreach (var (id, name) in targets)
        {
            if (_running.ContainsKey(id)) continue; // already running — skip
            var result = await StartServiceAsync(id, CancellationToken.None);
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
        // Everything tracked; every service with a start or stop in flight (its stop waits for
        // that to finish, so a start that launches meanwhile is still caught); and every service
        // with a run still recorded as active: the stop of such a service reaches a process an
        // earlier session left running, or finalizes the row of one that has died. A stop only
        // counts when something was actually running.
        var targets = _running.Values.ToDictionary(r => r.DevServiceId, r => r.ServiceName);
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var names = await db.DevServices.ToDictionaryAsync(s => s.Id, s => s.Name);
            foreach (var serviceId in _serviceLocks.Keys.Where(IsServiceBusy))
            {
                targets.TryAdd(serviceId, names.GetValueOrDefault(serviceId) ?? serviceId.ToString());
            }

            var activeRunServiceIds = await db.ServiceRuns
                .Where(r => ActiveStatuses.Contains(r.Status) && r.ProcessId != null)
                .Select(r => r.DevServiceId)
                .Distinct()
                .ToListAsync();
            foreach (var serviceId in activeRunServiceIds)
            {
                targets.TryAdd(serviceId, names.GetValueOrDefault(serviceId) ?? serviceId.ToString());
            }
        }

        // Stop concurrently: each stop can take StopTimeoutSeconds plus the kill fallback,
        // and one at a time that quickly outlasts the host's shutdown window.
        var results = await Task.WhenAll(
            targets.OrderBy(t => t.Value, StringComparer.OrdinalIgnoreCase).Select(async t =>
            {
                var result = await StopServiceAsync(t.Key, CancellationToken.None);
                return (result, outcome: new ServiceActionOutcome
                {
                    ServiceId = t.Key,
                    ServiceName = t.Value,
                    Success = result.Success,
                    Message = result.Message ?? result.Error,
                });
            }));
        var outcomes = results.Where(r => !r.result.NothingToStop).Select(r => r.outcome).ToList();
        return new StopAllResult { Stopped = outcomes.Count(o => o.Success), Outcomes = outcomes };
    }

    /// <summary>
    /// Startup: takes back under management the services an earlier DevDeck session launched
    /// and left running (by default DevDeck leaves them running when it exits). Without this
    /// they would show as stopped, the proxy would refuse them, and Start would launch a
    /// duplicate next to each. Returns how many were re-attached.
    /// </summary>
    public async Task<int> AdoptOrphanedRunsAsync(CancellationToken cancellationToken = default)
    {
        if (!ExecutionAllowed())
        {
            return 0;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var services = await db.DevServices.ToListAsync(cancellationToken);
        var adopted = 0;
        foreach (var service in services)
        {
            if (await TryAdoptLiveRunAsync(db, service) is not null)
            {
                adopted++;
            }
        }
        return adopted;
    }

    // Re-attaches the newest still-running run of the service, if there is one and nothing is
    // tracked for it yet. Callers outside startup hold the service's lock.
    private async Task<RunningProcessInfo?> TryAdoptLiveRunAsync(DevDeckDbContext db, DevService service)
    {
        if (_running.ContainsKey(service.Id))
        {
            return null;
        }

        var activeRuns = await db.ServiceRuns
            .Where(r => r.DevServiceId == service.Id && ActiveStatuses.Contains(r.Status) && r.ProcessId != null)
            .OrderByDescending(r => r.StartedUtc)
            .ToListAsync();

        foreach (var run in activeRuns)
        {
            var process = RunProcessMatcher.TryGetSameRunProcess(
                run.ProcessId!.Value,
                run.StartedUtc,
                run.ProcessStartKey,
                ex => _logger.LogDebug(ex, "Could not inspect process {ProcessId} of run {RunId}", run.ProcessId, run.Id));
            if (process is null)
            {
                continue;
            }

            var info = Adopt(service, run, process);
            if (info is not null)
            {
                // A run from before start keys were recorded: record it now, so a later clock
                // step can't make this process unrecognisable.
                if (run.ProcessStartKey is null && ProcessTree.TryGetIdentity(run.ProcessId.Value, out var identity))
                {
                    run.ProcessStartKey = identity.StartKey;
                    await SaveQuietlyAsync(db, run.Id);
                }
                return info;
            }
        }

        return null;
    }

    private RunningProcessInfo? Adopt(DevService service, ServiceRun run, Process process)
    {
        var pid = run.ProcessId!.Value;
        var logPath = run.LogFilePath ?? DevDeckPaths.LogFilePathFor(service.Name, run.Id, run.StartedUtc);
        var info = new RunningProcessInfo
        {
            DevServiceId = service.Id,
            ServiceRunId = run.Id,
            ServiceName = service.Name,
            Process = process,
            StartedUtc = run.StartedUtc,
            LogFilePath = logPath,
            Port = service.Port,
            Url = service.Url,
            Status = ProcessStatus.Running,
            IsAdopted = true,
            ProcessGroup = ProcessTree.GetProcessGroup(pid) == pid ? pid : null,
        };

        if (!_running.TryAdd(service.Id, info))
        {
            process.Dispose();
            return null;
        }

        var handled = 0;
        process.Exited += async (_, _) =>
        {
            var stopRequested = info.Status == ProcessStatus.Stopping;
            if (Interlocked.Exchange(ref handled, 1) == 1) return;
            try
            {
                await HandleProcessExitedAsync(service.Id, run.Id, logPath, process, info, stopRequested);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to finalize exited service {ServiceId}", service.Id);
            }
        };
        try
        {
            process.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not watch re-attached process {ProcessId}", pid);
        }

        AppendSystemLine(service.Id, run.Id, logPath,
            $"Re-attached to PID {pid}, left running by an earlier DevDeck session; its output is no longer captured.");
        _healthStatusCache.MarkStarting(service.Id, PostStartHealthWarmup);
        _logger.LogInformation("Re-attached service {ServiceName} (PID {ProcessId}) from an earlier session", service.Name, pid);

        // It may have exited between the liveness check and the watch being set up.
        if (SafeHasExited(process) && Interlocked.Exchange(ref handled, 1) == 0)
        {
            _ = HandleProcessExitedAsync(service.Id, run.Id, logPath, process, info, stopRequested: false);
        }
        return info;
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

    public void AppendProxyLog(RunningProcessInfo info, string text) =>
        WriteLine(info.DevServiceId, info.ServiceRunId, info.LogFilePath, "PRX", text);

    private void AppendSystemLine(int serviceId, long runId, string logPath, string text) =>
        WriteLine(serviceId, runId, logPath, "SYS", text);

    private void AppendOptionalSystemLine(int serviceId, long runId, string? logPath, string text)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            AppendSystemLine(serviceId, runId, logPath, text);
        }
    }

    // Untracked runs still recorded as active: processes an earlier DevDeck session left
    // running (stopped like a tracked one, tree and all), or rows of ones that have since died
    // (just finalized).
    private async Task<StopServiceResult> StopOrphanedServiceRunsAsync(int serviceId, long? excludeRunId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var activeRuns = await db.ServiceRuns
            .Where(r => r.DevServiceId == serviceId && ActiveStatuses.Contains(r.Status) && r.ProcessId != null)
            .OrderByDescending(r => r.StartedUtc)
            .ToListAsync();

        var stoppedPids = new List<int>();
        var failedPids = new List<int>();
        long? lastRunId = null;
        foreach (var run in activeRuns.Where(r => r.Id != excludeRunId))
        {
            var pid = run.ProcessId!.Value;
            using var process = RunProcessMatcher.TryGetSameRunProcess(
                pid,
                run.StartedUtc,
                run.ProcessStartKey,
                ex => _logger.LogDebug(ex, "Could not inspect orphaned process {ProcessId} for service {ServiceId}", pid, serviceId));

            if (process is null)
            {
                // Died while nobody was watching: record that instead of reporting it as running.
                run.StoppedUtc ??= DateTimeOffset.UtcNow;
                run.Status = run.Status == ProcessStatusNames.Starting ? ProcessStatusNames.FailedToStart : ProcessStatusNames.Stopped;
                continue;
            }

            lastRunId = run.Id;
            AppendOptionalSystemLine(serviceId, run.Id, run.LogFilePath, $"Stop requested for orphaned process PID {pid}");
            run.Status = ProcessStatusNames.Stopping;
            run.StoppedUtc = null;
            await SaveQuietlyAsync(db, run.Id);

            var killed = false;
            var outcome = new StopOutcome(false, false, 0);
            try
            {
                var group = ProcessTree.GetProcessGroup(pid) == pid ? pid : (int?)null;
                outcome = await ProcessTerminator.StopTreeAsync(
                    process,
                    group,
                    TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.StopTimeoutSeconds)),
                    line => AppendOptionalSystemLine(serviceId, run.Id, run.LogFilePath, line),
                    () => killed = true);
            }
            catch (Exception ex)
            {
                AppendOptionalSystemLine(serviceId, run.Id, run.LogFilePath, $"Error stopping orphaned process: {ex.Message}");
                _logger.LogWarning(ex, "Failed to stop orphaned process {ProcessId} for service {ServiceId}", pid, serviceId);
            }

            if (outcome.Exited || SafeHasExited(process))
            {
                run.StoppedUtc = DateTimeOffset.UtcNow;
                run.Status = killed ? ProcessStatusNames.Killed : ProcessStatusNames.Stopped;
                stoppedPids.Add(pid);
                AppendOptionalSystemLine(serviceId, run.Id, run.LogFilePath, "Orphaned process stopped");
            }
            else
            {
                failedPids.Add(pid);
            }
            if (!string.IsNullOrWhiteSpace(run.LogFilePath))
            {
                _logFileWriter.Close(run.LogFilePath, allowReopen: false);
            }
        }

        await SaveQuietlyAsync(db, null);

        if (stoppedPids.Count == 0 && failedPids.Count == 0)
        {
            return new StopServiceResult { ServiceId = serviceId, Success = false, NothingToStop = true, Error = "Service is not running." };
        }

        return new StopServiceResult
        {
            ServiceId = serviceId,
            RunId = lastRunId,
            Success = failedPids.Count == 0,
            Message = failedPids.Count == 0 ? $"Stopped orphaned PID {string.Join(", ", stoppedPids)}" : null,
            Error = failedPids.Count == 0 ? null : $"Timed out waiting for orphaned PID {string.Join(", ", failedPids)} to exit",
        };
    }

    private async Task SaveQuietlyAsync(DevDeckDbContext db, long? runId)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ServiceRun {RunId}", runId);
        }
    }

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

    private async Task HandleProcessExitedAsync(
        int serviceId, long runId, string logPath, Process process, RunningProcessInfo? info, bool stopRequested)
    {
        var exitCode = TryGetExitCode(process);
        var adopted = info?.IsAdopted == true;

        // Let the async stdout/stderr readers deliver buffered tail lines before the exit
        // line is written. WaitForExitAsync returns once both streams hit EOF (usually at
        // once) — the readers are attached by now, as this handler only runs after the start
        // gate opens; the cap stops a grandchild holding the inherited pipe open from stalling us.
        using (var drain = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { await process.WaitForExitAsync(drain.Token); } catch { /* capped / disposed */ }
        }

        AppendSystemLine(serviceId, runId, logPath, exitCode is null && adopted
            ? "Process exited (exit code unavailable: it was started by an earlier DevDeck session)"
            : $"Process exited with code {exitCode?.ToString() ?? "?"}");

        // Decided from the in-memory state the stop path sets, not from the run row's status:
        // a concurrent Stopping write could land on either side of this handler's update.
        var status = adopted && exitCode is null && !stopRequested
            ? ProcessStatusNames.Stopped
            : ProcessStatusNames.ResolveExitStatus(
                stopRequested ? ProcessStatusNames.Stopping : ProcessStatusNames.Running,
                exitCode,
                info?.KillIssued == true);

        // Finalize the run while it is still in the running map: RunHistoryRefreshService
        // leaves tracked runs to this handler (and snapshots the map before reading rows),
        // so it can't race this update, and Restart/Stop (which wait for the removal below)
        // see the new run strictly after this run's exit line and final status.
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var run = await db.ServiceRuns.FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null)
            {
                run.StoppedUtc = DateTimeOffset.UtcNow;
                run.ExitCode = exitCode;
                run.Status = status;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update ServiceRun {RunId} on exit", runId);
        }

        if (info is not null)
        {
            _running.TryRemove(new KeyValuePair<int, RunningProcessInfo>(serviceId, info));
        }
        _healthStatusCache.RemoveService(serviceId);
        _logFileWriter.Close(logPath, allowReopen: false);

        // Release the OS handle; every other reader of this Process goes through the
        // Safe* helpers, which treat a disposed process as exited.
        try { process.Dispose(); } catch { /* best-effort */ }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static int? TryGetExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    private List<RenderedEnvironmentVariable> RenderEnvironment(DevService service, IReadOnlyDictionary<string, string?> values) =>
        service.EnvironmentVariables
            .Select(env => new RenderedEnvironmentVariable(env.Key, _renderer.Render(env.Value, values).Text, env.IsSecret))
            .ToList();

    private static string? EffectivePathValue(IReadOnlyCollection<RenderedEnvironmentVariable> environment)
    {
        var pathOverride = environment.LastOrDefault(e => string.Equals(e.Key, "PATH", StringComparison.OrdinalIgnoreCase));
        return pathOverride?.Value ?? Environment.GetEnvironmentVariable("PATH");
    }

    private static bool IsAzureFunctionServiceType(string serviceType) =>
        string.Equals(serviceType, AzureFunctionServiceType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(serviceType, AzureFunctionsServiceType, StringComparison.OrdinalIgnoreCase);

    private static void AddAzureFunctionsDefaults(List<RenderedEnvironmentVariable> environment, bool isAzureFunction)
    {
        if (!isAzureFunction ||
            environment.Any(e => string.Equals(e.Key, AzureWebJobsStorageKey, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        environment.Add(new RenderedEnvironmentVariable(
            AzureWebJobsStorageKey,
            AzureWebJobsStorageDevelopmentValue,
            IsSecret: false));
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

    // Conditional on the run not being finished yet, so a Stopping that arrives after the exit
    // handler's final write can't overwrite it; and not cancellable, so an aborted request
    // can't skip it halfway through a stop.
    private async Task MarkRunStoppingAsync(long runId)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await db.ServiceRuns
                .Where(r => r.Id == runId && r.StoppedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ProcessStatusNames.Stopping));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark ServiceRun {RunId} as {Status}", runId, ProcessStatusNames.Stopping);
        }
    }

    private sealed record RenderedEnvironmentVariable(string Key, string Value, bool IsSecret);

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
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

    // Container disposal at host shutdown: release process handles and locks. This does
    // NOT stop the child processes — that is StopServicesOnShutdownHostedService's job,
    // under DevDeck:StopServicesOnShutdown (on by default).
    public void Dispose()
    {
        foreach (var info in _running.Values)
        {
            try { info.Process.Dispose(); } catch { /* best-effort */ }
        }
        _running.Clear();

        foreach (var serviceLock in _serviceLocks.Values)
        {
            try { serviceLock.Dispose(); } catch { /* best-effort */ }
        }
        _serviceLocks.Clear();
    }
}
