using System.Diagnostics;
using System.Net.Sockets;
using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Logs;
using Microsoft.Extensions.Options;

namespace DevDeck.Web.Services.Runtime;

/// <summary>
/// Ensures the Azurite storage emulator is up before an Azure Functions host starts —
/// the Functions runtime needs <c>AzureWebJobsStorage</c> (blob/queue/table) and fails on
/// startup when it is missing. If the emulator's ports are already listening it is treated as
/// healthy and reused (whether DevDeck or the user launched it); otherwise DevDeck launches the
/// global <c>azurite</c> CLI as a managed background process and waits for its ports to come up.
/// </summary>
public interface IAzuriteSupervisor
{
    Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken);
}

public sealed record AzuriteReadyResult(bool Success, string? Error = null);

public sealed class AzuriteSupervisor : IAzuriteSupervisor, IAsyncDisposable
{
    private readonly CommandExecutableResolver _resolver;
    private readonly LogFileWriter _logFileWriter;
    private readonly IOptionsMonitor<DevDeckOptions> _options;
    private readonly ILogger<AzuriteSupervisor> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    private static readonly string Workspace = Path.Combine(DevDeckPaths.RootFolder, "azurite");
    private static readonly string LogFile = Path.Combine(DevDeckPaths.LogsFolder, "azurite.log");

    public AzuriteSupervisor(
        CommandExecutableResolver resolver,
        LogFileWriter logFileWriter,
        IOptionsMonitor<DevDeckOptions> options,
        ILogger<AzuriteSupervisor> logger)
    {
        _resolver = resolver;
        _logFileWriter = logFileWriter;
        _options = options;
        _logger = logger;
    }

    public async Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue.Azurite;
        var ports = new[] { opts.BlobPort, opts.QueuePort, opts.TablePort };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await AllPortsOpenAsync(ports, cancellationToken))
            {
                log($"Azurite already running (blob:{opts.BlobPort}, queue:{opts.QueuePort}, table:{opts.TablePort}).");
                return new AzuriteReadyResult(true);
            }

            if (_process is { HasExited: false } existing)
            {
                log($"Waiting for Azurite (PID {SafePid(existing)}) to become ready...");
            }
            else
            {
                var launch = TryLaunch(opts, log);
                if (launch is not null)
                {
                    return new AzuriteReadyResult(false, launch);
                }
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, opts.StartupTimeoutSeconds));
            if (await WaitForPortsAsync(ports, timeout, cancellationToken))
            {
                log("Azurite is ready.");
                return new AzuriteReadyResult(true);
            }

            if (_process is { HasExited: true })
            {
                return new AzuriteReadyResult(false,
                    $"Azurite exited during startup (see {LogFile}).");
            }

            return new AzuriteReadyResult(false,
                $"Timed out after {timeout.TotalSeconds:0}s waiting for Azurite ports {opts.BlobPort}/{opts.QueuePort}/{opts.TablePort}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns an error message on failure, or null when the process started.</summary>
    private string? TryLaunch(AzuriteOptions opts, Action<string> log)
    {
        var executable = _resolver.ResolveForLaunch(opts.Command);
        Process? process = null;
        try
        {
            Directory.CreateDirectory(Workspace);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--silent");
            psi.ArgumentList.Add("--skipApiVersionCheck");
            psi.ArgumentList.Add("--location");
            psi.ArgumentList.Add(Workspace);
            psi.ArgumentList.Add("--blobPort");
            psi.ArgumentList.Add(opts.BlobPort.ToString());
            psi.ArgumentList.Add("--queuePort");
            psi.ArgumentList.Add(opts.QueuePort.ToString());
            psi.ArgumentList.Add("--tablePort");
            psi.ArgumentList.Add(opts.TablePort.ToString());

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var processLocal = process;
            process.OutputDataReceived += (_, e) => WriteAzuriteLog("OUT", e.Data);
            process.ErrorDataReceived += (_, e) => WriteAzuriteLog("ERR", e.Data);
            process.Exited += (_, _) =>
            {
                _logFileWriter.Close(LogFile);
                if (ReferenceEquals(_process, processLocal))
                {
                    _process = null;
                }
            };

            process.Start();
            // Track immediately after Start so a BeginOutputReadLine failure below can't
            // leave a live Azurite running untracked (DisposeAsync would never reach it).
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            log($"Launched Azurite (PID {SafePid(process)}) on ports {opts.BlobPort}/{opts.QueuePort}/{opts.TablePort}; logging to {LogFile}.");
            return null;
        }
        catch (Exception ex)
        {
            // Only dispose the handle when the process never became tracked (Start failed);
            // a tracked process stays under _process for the exit handler / DisposeAsync.
            if (process is not null && !ReferenceEquals(_process, process))
            {
                try { process.Dispose(); } catch { /* best-effort */ }
            }
            _logger.LogWarning(ex, "Failed to launch Azurite via '{Command}'", opts.Command);
            return $"Could not launch Azurite ('{opts.Command}'): {ex.Message}. Install it with 'npm install -g azurite'.";
        }
    }

    private void WriteAzuriteLog(string stream, string? text)
    {
        if (text is null) return;
        _logFileWriter.Append(LogFile, new LogLine
        {
            Timestamp = DateTimeOffset.UtcNow,
            DevServiceId = 0,
            ServiceRunId = 0,
            Stream = stream,
            Text = text,
        });
    }

    private static async Task<bool> WaitForPortsAsync(int[] ports, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await AllPortsOpenAsync(ports, cancellationToken))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        return await AllPortsOpenAsync(ports, cancellationToken);
    }

    private static async Task<bool> AllPortsOpenAsync(int[] ports, CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            if (!await IsPortOpenAsync(port, cancellationToken))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync("127.0.0.1", port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // best-effort shutdown
        }
        finally
        {
            process.Dispose();
            _logFileWriter.Close(LogFile);
            _gate.Dispose();
        }
    }
}
