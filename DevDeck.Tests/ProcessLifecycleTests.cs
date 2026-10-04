using System.Diagnostics;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDeck.Tests;

// Real processes through the real DevDeckProcessManager: stopping whole process trees,
// re-attaching services an earlier session left running, stop commands, and the run statuses
// those paths record. Most of these exercise POSIX behaviour (process groups, SIGTERM) and
// return early on Windows, which has its own test.
public sealed class ProcessLifecycleTests : IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"devdeck-lifecycle-{Guid.NewGuid():N}.db");
    private readonly TestDbContextFactory _factory;
    private readonly DirectoryInfo _workDir = Directory.CreateTempSubdirectory("devdeck-lifecycle-");
    private readonly LogFileWriter _logFileWriter = new();
    private readonly List<Process> _spawned = new();
    private readonly List<DevDeckProcessManager> _managers = new();
    private readonly List<int> _spawnedPids = new();

    public ProcessLifecycleTests()
    {
        _factory = new TestDbContextFactory($"Data Source={_databaseFile}");
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (var manager in _managers)
        {
            foreach (var info in manager.GetRunningProcesses())
            {
                Kill(info.Process);
            }
        }
        foreach (var process in _spawned)
        {
            Kill(process);
            process.Dispose();
        }
        foreach (var pid in _spawnedPids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Kill(process);
            }
            catch (ArgumentException)
            {
                // already gone
            }
        }
        _logFileWriter.DisposeAsync().AsTask().Wait();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_databaseFile); } catch (IOException) { /* a late exit handler may still hold it */ }
        try { _workDir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Stop_ends_the_whole_tree_when_the_root_does_not_pass_the_signal_on()
    {
        if (OperatingSystem.IsWindows()) return;

        // dash runs `sleep` as a child (a command follows it, so no exec) and dies on SIGTERM
        // without passing it on — the shape of npm → sh → vite.
        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 300; echo after\"");
        var manager = CreateManager();

        (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();
        var info = manager.GetRunningProcess(serviceId)!;
        var root = info.Process;
        var child = await WaitForDescendantAsync(root.Id);
        if (OperatingSystem.IsLinux())
        {
            // Started via setsid: its own session and process group, out of DevDeck's.
            info.ProcessGroup.Should().Be(root.Id);
            ProcessTree.GetProcessGroup(root.Id).Should().Be(root.Id);
        }

        var stop = await manager.StopServiceAsync(serviceId, CancellationToken.None);

        stop.Success.Should().BeTrue();
        ProcessTree.IsAlive(child).Should().BeFalse("the dev server under the shell must not survive Stop");
        (await RunAsync(serviceId)).Status.Should().Be(ProcessStatusNames.Stopped);
    }

    [Fact]
    public async Task Terminator_signals_descendants_of_a_process_without_its_own_group()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Spawn("/bin/sh", "-c \"sleep 300; echo after\"");
        var child = await WaitForDescendantAsync(root.Id);
        var lines = new List<string>();

        var outcome = await ProcessTerminator.StopTreeAsync(root, processGroup: null, TimeSpan.FromSeconds(5), lines.Add, () => { });

        outcome.Exited.Should().BeTrue();
        outcome.KillIssued.Should().BeFalse();
        ProcessTree.IsAlive(child).Should().BeFalse();
        lines.Should().Contain(l => l.Contains("SIGTERM") && l.Contains("child process"));
    }

    [Fact]
    public async Task Leftovers_that_ignore_SIGTERM_are_killed_after_the_grace_period()
    {
        if (OperatingSystem.IsWindows()) return;

        // The subshell ignores SIGTERM and keeps that across exec, so sleep outlives the root.
        var root = Spawn("/bin/sh", "-c \"(trap '' TERM; exec sleep 300) & wait\"");
        var child = await WaitForDescendantAsync(root.Id);
        var killIssued = false;

        var outcome = await ProcessTerminator.StopTreeAsync(root, processGroup: null, TimeSpan.FromSeconds(1), _ => { }, () => killIssued = true);

        outcome.KillIssued.Should().BeTrue();
        killIssued.Should().BeTrue();
        ProcessTree.IsAlive(child).Should().BeFalse();
    }

    [Fact]
    public async Task Services_left_running_by_an_earlier_session_are_reattached_and_can_be_stopped()
    {
        if (OperatingSystem.IsWindows()) return;

        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 300\"");
        var orphan = Spawn("/bin/sh", "-c \"exec sleep 300\"");
        var runId = await SeedRunAsync(serviceId, orphan.Id, ProcessStatusNames.Running);
        var manager = CreateManager();

        (await manager.AdoptOrphanedRunsAsync()).Should().Be(1);

        var info = manager.GetRunningProcess(serviceId);
        info.Should().NotBeNull();
        info!.IsAdopted.Should().BeTrue();
        info.ServiceRunId.Should().Be(runId);

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);
        start.Success.Should().BeFalse();
        start.Error.Should().Contain("already running");

        var stop = await manager.StopServiceAsync(serviceId, CancellationToken.None);
        stop.Success.Should().BeTrue();
        orphan.WaitForExit(5000).Should().BeTrue();
        manager.GetRunningProcess(serviceId).Should().BeNull();
        (await RunAsync(serviceId)).Status.Should().Be(ProcessStatusNames.Stopped);
    }

    [Fact]
    public async Task Start_reattaches_a_live_run_instead_of_launching_a_duplicate()
    {
        if (OperatingSystem.IsWindows()) return;

        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 300\"");
        var orphan = Spawn("/bin/sh", "-c \"exec sleep 300\"");
        await SeedRunAsync(serviceId, orphan.Id, ProcessStatusNames.Running);
        var manager = CreateManager();

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);

        start.Success.Should().BeFalse();
        start.Error.Should().Contain("already running");
        manager.GetRunningProcess(serviceId)!.Process.Id.Should().Be(orphan.Id);
        await using var db = _factory.CreateDbContext();
        (await db.ServiceRuns.CountAsync(r => r.DevServiceId == serviceId)).Should().Be(1, "no second run was launched");
    }

    [Fact]
    public async Task Stop_runs_the_configured_stop_command_before_signalling()
    {
        if (OperatingSystem.IsWindows()) return;

        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 300\"", s =>
        {
            s.StopCommand = "/bin/sh";
            s.StopArguments = "-c \"echo stopped-by-{name} > stop-marker.txt\"";
        });
        var manager = CreateManager();
        (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();

        (await manager.StopServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();

        var marker = Path.Combine(_workDir.FullName, "stop-marker.txt");
        File.Exists(marker).Should().BeTrue();
        (await File.ReadAllTextAsync(marker)).Trim().Should().StartWith("stopped-by-svc-");
    }

    [Fact]
    public async Task Relative_start_command_is_resolved_against_the_working_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        var script = Path.Combine(_workDir.FullName, "run.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho relative-start-ok\nexec sleep 300\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var serviceId = await SeedServiceAsync("./run.sh", null);
        var manager = CreateManager();

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);

        start.Success.Should().BeTrue(start.Error);
        await WaitUntilAsync(() => manager.GetLiveLogs(serviceId).Any(l => l.Text == "relative-start-ok"));
        await manager.StopServiceAsync(serviceId, CancellationToken.None);
    }

    [Fact]
    public async Task A_crash_is_not_relabelled_as_stopped_by_a_stop_that_arrives_after_it()
    {
        if (OperatingSystem.IsWindows()) return;

        // The background sleep keeps the output pipes open, so the run stays tracked for the
        // exit handler's drain window after the crash — the window a Stop can land in.
        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 3 & exit 3\"");
        var manager = CreateManager();
        (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();
        var root = manager.GetRunningProcess(serviceId)!.Process;
        await WaitUntilAsync(() => SafeHasExited(root));

        await manager.StopServiceAsync(serviceId, CancellationToken.None);

        await WaitUntilAsync(() => manager.GetRunningProcess(serviceId) is null);
        var run = await RunAsync(serviceId);
        run.Status.Should().Be(ProcessStatusNames.Crashed);
        run.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task DevDecks_own_hosting_urls_are_not_inherited_by_services()
    {
        if (OperatingSystem.IsWindows()) return;

        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"echo urls=[$ASPNETCORE_URLS]; exec sleep 300\"");
        var manager = CreateManager();
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5119");
        try
        {
            (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous);
        }

        await WaitUntilAsync(() => manager.GetLiveLogs(serviceId).Any(l => l.Text.StartsWith("urls=")));
        manager.GetLiveLogs(serviceId).Should().Contain(l => l.Text == "urls=[]");
        await manager.StopServiceAsync(serviceId, CancellationToken.None);
    }

    [Fact]
    public async Task Failed_start_releases_its_run_log()
    {
        var serviceId = await SeedServiceAsync("devdeck-missing-command-" + Guid.NewGuid().ToString("N"), null);
        var manager = CreateManager();

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);

        start.Success.Should().BeFalse();
        var run = await RunAsync(serviceId);
        run.Status.Should().Be(ProcessStatusNames.FailedToStart);
        _logFileWriter.IsOpen(run.LogFilePath!).Should().BeFalse();
    }

    [Fact]
    public async Task Profile_start_reports_members_that_need_nothing_as_skipped_not_failed()
    {
        var disabled = await SeedServiceAsync("/bin/sh", null, s => s.Enabled = false);
        var passthru = await SeedServiceAsync("/bin/sh", null, s => { s.UseExternalInstance = true; s.ExternalPort = 7071; });
        int profileId;
        await using (var db = _factory.CreateDbContext())
        {
            var profile = new LaunchProfile { Name = "stack" };
            profile.Services.Add(new LaunchProfileService { DevServiceId = disabled, StartOrder = 0, StartDelaySeconds = 30 });
            profile.Services.Add(new LaunchProfileService { DevServiceId = passthru, StartOrder = 1, StartDelaySeconds = 30 });
            db.LaunchProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }
        var manager = CreateManager();

        var stopwatch = Stopwatch.StartNew();
        var result = await manager.StartProfileAsync(profileId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Outcomes.Should().HaveCount(2).And.OnlyContain(o => o.Skipped && o.Success);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "skipped members cost no start delay");
    }

    [Fact]
    public async Task StopAll_closes_out_dead_runs_without_reporting_a_failure()
    {
        var serviceId = await SeedServiceAsync("/bin/sh", null);
        var exited = Spawn(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? "/c exit 0" : "-c \"exit 0\"");
        exited.WaitForExit(5000);
        var runId = await SeedRunAsync(serviceId, exited.Id, ProcessStatusNames.Running);
        var manager = CreateManager();

        var result = await manager.StopAllAsync(CancellationToken.None);

        result.Outcomes.Should().BeEmpty();
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(ProcessStatusNames.Stopped);
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Shared_build_servers_in_the_tree_are_left_running()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;

        // `dotnet run` leaves Roslyn's VBCSCompiler (shared by every build of the user's) in the
        // service's tree and process group; stopping the service must not take it down. The
        // stand-in is one process whose command line names it, as the real server's does.
        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"/bin/bash -c 'exec -a VBCSCompiler sleep 300' & sleep 300; echo after\"");
        var manager = CreateManager();
        (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();
        var root = manager.GetRunningProcess(serviceId)!.Process;
        var buildServer = await WaitForAsync(() => DescendantsIncludingSpared(root.Id).FirstOrDefault(ProcessTree.IsSharedBuildServer));
        _spawnedPids.Add(buildServer);

        var stop = await manager.StopServiceAsync(serviceId, CancellationToken.None);

        stop.Success.Should().BeTrue();
        stop.Message.Should().Be("Stopped", "the stop must not wait for, or kill, the build server");
        ProcessTree.TryGetIdentity(buildServer, out _).Should().BeTrue();
    }

    [Fact]
    public void Build_servers_are_recognised_by_their_command_line()
    {
        var (file, serverArgs, plainArgs) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c \"ping -n 30 127.0.0.1 >nul & rem VBCSCompiler.dll -pipename:x\"", "/c \"ping -n 30 127.0.0.1 >nul\"")
            : ("/bin/sh", "-c \"sleep 30 # dotnet MSBuild.dll /nodemode:1 /nodeReuse:true\"", "-c \"sleep 30\"");
        var server = Spawn(file, serverArgs);
        var plain = Spawn(file, plainArgs);

        ProcessTree.IsSharedBuildServer(server.Id).Should().BeTrue();
        ProcessTree.IsSharedBuildServer(plain.Id).Should().BeFalse();
    }

    [Fact]
    public async Task A_script_whose_interpreter_cannot_run_fails_to_start()
    {
        if (OperatingSystem.IsWindows()) return;

        // A CRLF shebang ("#!/bin/sh\r") is common for repos checked out under /mnt/c in WSL.
        var script = Path.Combine(_workDir.FullName, "crlf.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\r\necho hi\r\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var serviceId = await SeedServiceAsync("./crlf.sh", null);
        var manager = CreateManager();

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);

        start.Success.Should().BeFalse();
        (await RunAsync(serviceId)).Status.Should().Be(ProcessStatusNames.FailedToStart);
    }

    [Fact]
    public void Only_files_the_kernel_can_run_are_started_through_setsid()
    {
        if (OperatingSystem.IsWindows()) return;

        string Script(string name, string content)
        {
            var path = Path.Combine(_workDir.FullName, name);
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        ProcessSessions.CanExecDirectly("/bin/sh").Should().BeTrue();
        ProcessSessions.CanExecDirectly(Script("ok.sh", "#!/bin/sh -e\necho ok\n")).Should().BeTrue();
        ProcessSessions.CanExecDirectly(Script("crlf.sh", "#!/bin/sh\r\necho ok\r\n")).Should().BeFalse();
        ProcessSessions.CanExecDirectly(Script("noshebang.sh", "echo ok\n")).Should().BeFalse();
        ProcessSessions.CanExecDirectly(Script("missing-interp.sh", "#!/no/such/interpreter\n")).Should().BeFalse();
    }

    [Fact]
    public async Task Once_shutdown_begins_no_start_launches_anything()
    {
        var serviceId = await SeedServiceAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", null);
        var manager = CreateManager();
        manager.BeginShutdown();

        var start = await manager.StartServiceAsync(serviceId, CancellationToken.None);

        start.Success.Should().BeFalse();
        start.Error.Should().Contain("shutting down");
        manager.GetRunningProcess(serviceId).Should().BeNull();
    }

    [Fact]
    public async Task A_start_still_waiting_for_azurite_when_shutdown_begins_is_not_launched()
    {
        var serviceId = await SeedServiceAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? "/c ping -n 300 127.0.0.1" : "-c \"sleep 300\"",
            s => s.ServiceType = "AzureFunction");
        var azurite = new GatedAzurite();
        var manager = CreateManager(azurite: azurite);

        var start = manager.StartServiceAsync(serviceId, CancellationToken.None);
        await azurite.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        manager.BeginShutdown();
        var stopAll = manager.StopAllAsync(CancellationToken.None);
        azurite.Release.TrySetResult();

        (await start).Success.Should().BeFalse();
        await stopAll;
        manager.GetRunningProcess(serviceId).Should().BeNull();
        (await RunAsync(serviceId)).Status.Should().Be(ProcessStatusNames.FailedToStart);
    }

    [Fact]
    public async Task A_process_this_session_did_not_start_is_reattached_and_seen_to_exit()
    {
        if (OperatingSystem.IsWindows()) return;

        // Started by a shell that exits at once, so it is not DevDeck's (nor the test's) child —
        // the real situation after a DevDeck restart: no exit code, exit seen by watching.
        var serviceId = await SeedServiceAsync("/bin/sh", "-c \"sleep 300\"");
        var launcher = Spawn("/bin/sh", "-c \"sleep 2 > /dev/null 2>&1 & echo $!\"", redirectOutput: true);
        var pid = int.Parse((await launcher.StandardOutput.ReadLineAsync())!.Trim());
        _spawnedPids.Add(pid);
        await SeedRunAsync(serviceId, pid, ProcessStatusNames.Running);
        var manager = CreateManager();

        (await manager.AdoptOrphanedRunsAsync()).Should().Be(1);
        manager.GetRunningProcess(serviceId)!.IsAdopted.Should().BeTrue();

        await WaitUntilAsync(() => manager.GetRunningProcess(serviceId) is null);
        var run = await RunAsync(serviceId);
        run.Status.Should().Be(ProcessStatusNames.Stopped, "its exit code can't be known, so it isn't reported as a crash");
        run.StoppedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_run_is_recognised_by_its_start_key_even_after_the_clock_was_stepped()
    {
        // WSL resyncs its clock after the host sleeps. On Linux .NET derives Process.StartTime
        // from the current clock, so the process now seems to have started long after its run
        // did — the recorded start key is what still identifies it.
        var serviceId = await SeedServiceAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", null);
        var orphan = OperatingSystem.IsWindows() ? Spawn("cmd.exe", "/c ping -n 300 127.0.0.1") : Spawn("/bin/sh", "-c \"exec sleep 300\"");
        ProcessTree.TryGetIdentity(orphan.Id, out var identity).Should().BeTrue();
        await SeedRunAsync(serviceId, orphan.Id, ProcessStatusNames.Running,
            startedUtc: DateTimeOffset.UtcNow.AddHours(-2), processStartKey: identity.StartKey);
        var manager = CreateManager();

        (await manager.AdoptOrphanedRunsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_live_process_with_a_different_start_key_is_not_taken_for_the_run()
    {
        // The PID was reused by an unrelated process.
        var serviceId = await SeedServiceAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", null);
        var other = OperatingSystem.IsWindows() ? Spawn("cmd.exe", "/c ping -n 300 127.0.0.1") : Spawn("/bin/sh", "-c \"exec sleep 300\"");
        ProcessTree.TryGetIdentity(other.Id, out var identity).Should().BeTrue();
        await SeedRunAsync(serviceId, other.Id, ProcessStatusNames.Running,
            startedUtc: DateTimeOffset.UtcNow, processStartKey: identity.StartKey - 10 * TimeSpan.TicksPerSecond);
        var manager = CreateManager();

        (await manager.AdoptOrphanedRunsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Windows_stop_asks_console_services_to_exit_with_ctrl_c()
    {
        if (!OperatingSystem.IsWindows()) return;

        // ping exits on Ctrl+C; without it DevDeck would have to kill the tree.
        var serviceId = await SeedServiceAsync("ping", "-n 300 127.0.0.1");
        var manager = CreateManager();
        (await manager.StartServiceAsync(serviceId, CancellationToken.None)).Success.Should().BeTrue();
        await Task.Delay(500);

        var stop = await manager.StopServiceAsync(serviceId, CancellationToken.None);

        stop.Success.Should().BeTrue();
        manager.GetLiveLogs(serviceId).Should().Contain(l => l.Text == "Sent Ctrl+C");
        (await RunAsync(serviceId)).Status.Should().Be(ProcessStatusNames.Stopped);
    }

    private DevDeckProcessManager CreateManager(DevDeckOptions? options = null, IAzuriteSupervisor? azurite = null)
    {
        options ??= new DevDeckOptions { DevelopmentOnly = false, StopTimeoutSeconds = 5 };
        var manager = new DevDeckProcessManager(
            _factory,
            new ProcessLogBuffer(5000, 1000),
            _logFileWriter,
            new CommandTemplateRenderer(),
            new CommandExecutableResolver(),
            new TestOptionsMonitor<DevDeckOptions>(options),
            new TestWebHostEnvironment(),
            new HealthStatusCache(),
            azurite ?? new ReadyAzurite(),
            NullLogger<DevDeckProcessManager>.Instance);
        _managers.Add(manager);
        return manager;
    }

    private async Task<int> SeedServiceAsync(string command, string? arguments, Action<DevService>? configure = null)
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = "svc-" + Guid.NewGuid().ToString("N")[..8],
            ServiceType = "Custom",
            WorkingDirectory = _workDir.FullName,
            StartCommand = command,
            StartArguments = arguments,
        };
        configure?.Invoke(service);
        db.DevServices.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private async Task<long> SeedRunAsync(
        int serviceId, int processId, string status, DateTimeOffset? startedUtc = null, long? processStartKey = null)
    {
        await using var db = _factory.CreateDbContext();
        var run = new ServiceRun
        {
            DevServiceId = serviceId,
            StartedUtc = startedUtc ?? DateTimeOffset.UtcNow,
            ProcessId = processId,
            ProcessStartKey = processStartKey,
            Status = status,
            LogFilePath = Path.Combine(_workDir.FullName, $"run-{Guid.NewGuid():N}.log"),
        };
        db.ServiceRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<ServiceRun> RunAsync(int serviceId)
    {
        await using var db = _factory.CreateDbContext();
        return await db.ServiceRuns.Where(r => r.DevServiceId == serviceId).OrderByDescending(r => r.Id).FirstAsync();
    }

    private Process Spawn(string fileName, string arguments, bool redirectOutput = false)
    {
        var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = redirectOutput,
        })!;
        _spawned.Add(process);
        return process;
    }

    private static async Task<ProcessIdentity> WaitForDescendantAsync(int rootPid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var descendants = ProcessTree.GetDescendants(rootPid);
            if (descendants.Count > 0) return descendants[0];
            await Task.Delay(50);
        }
        throw new TimeoutException($"PID {rootPid} started no child process.");
    }

    // Descendants by parent PID without ProcessTree's build-server filter.
    private static IEnumerable<int> DescendantsIncludingSpared(int rootPid)
    {
        var children = Directory.EnumerateDirectories("/proc")
            .Select(d => int.TryParse(Path.GetFileName(d), out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .Select(pid =>
            {
                try
                {
                    var stat = File.ReadAllText($"/proc/{pid}/stat");
                    return (pid, ppid: int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1]));
                }
                catch
                {
                    return (pid, ppid: -1);
                }
            })
            .ToList();
        var result = new List<int>();
        var queue = new Queue<int>([rootPid]);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var (pid, _) in children.Where(c => c.ppid == parent))
            {
                result.Add(pid);
                queue.Enqueue(pid);
            }
        }
        return result;
    }

    private static async Task<int> WaitForAsync(Func<int> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value != 0) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condition not met in time.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class GatedAzurite : IAzuriteSupervisor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new AzuriteReadyResult(true);
        }
    }

    private sealed class ReadyAzurite : IAzuriteSupervisor
    {
        public Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken) =>
            Task.FromResult(new AzuriteReadyResult(true));
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "DevDeck.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DevDeckDbContext>
    {
        private readonly DbContextOptions<DevDeckDbContext> _options;
        public TestDbContextFactory(string connectionString) =>
            _options = new DbContextOptionsBuilder<DevDeckDbContext>().UseSqlite(connectionString).Options;
        public DevDeckDbContext CreateDbContext() => new(_options);
    }
}
