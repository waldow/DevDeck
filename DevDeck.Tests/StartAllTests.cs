using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;

namespace DevDeck.Tests;

// Exercises the real DevDeckProcessManager.StartAllAsync selection/ordering logic.
// Each service points at a missing working directory so StartServiceAsync fails fast at
// the directory check — no processes are spawned and no log files are written, keeping the
// test deterministic while still driving the genuine method.
public sealed class StartAllTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public StartAllTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task StartAll_targets_enabled_services_in_display_order_and_skips_disabled()
    {
        var alpha = await SeedServiceAsync("alpha", displayOrder: 2, enabled: true);
        var bravo = await SeedServiceAsync("bravo", displayOrder: 0, enabled: true);
        await SeedServiceAsync("charlie-disabled", displayOrder: 1, enabled: false);

        var manager = CreateManager();
        var result = await manager.StartAllAsync(CancellationToken.None);

        // Disabled service excluded; remaining ordered by DisplayOrder (bravo=0 before alpha=2).
        result.Outcomes.Select(o => o.ServiceId)
            .Should().BeEquivalentTo(new[] { bravo, alpha }, opts => opts.WithStrictOrdering());
        // All fail on the missing working directory, so nothing actually started.
        result.Started.Should().Be(0);
        result.Outcomes.Should().OnlyContain(o => !o.Success && o.Message!.Contains("Working directory does not exist"));
    }

    [Fact]
    public async Task AzureFunctions_start_injects_development_storage_and_runs_azurite_preflight()
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-azure-functions-");
        try
        {
            var serviceId = await SeedRunnableServiceAsync("func-plural-" + Guid.NewGuid().ToString("N"), temp.FullName, "AzureFunctions");
            var azurite = new StubAzuriteSupervisor();
            var manager = CreateManager(azurite);

            var result = await manager.StartServiceAsync(serviceId, CancellationToken.None);

            result.Success.Should().BeTrue();
            azurite.Calls.Should().Be(1);
            await WaitForProcessExitAsync(manager, serviceId);

            var log = await ReadRunLogAsync(result.RunId!.Value);
            log.Should().Contain("Ensuring Azurite storage emulator is running...");
            log.Should().Contain("Environment overrides: AzureWebJobsStorage=UseDevelopmentStorage=true");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AzureFunction_start_preserves_explicit_storage_override()
    {
        var temp = Directory.CreateTempSubdirectory("devdeck-azure-functions-");
        try
        {
            var serviceId = await SeedRunnableServiceAsync(
                "func-custom-storage-" + Guid.NewGuid().ToString("N"),
                temp.FullName,
                "AzureFunction",
                new ServiceEnvironmentVariable
                {
                    Key = "AzureWebJobsStorage",
                    Value = "UseDevelopmentStorage=true;CustomEndpoint=http://localhost:11000",
                    IsSecret = false,
                });

            var manager = CreateManager(new StubAzuriteSupervisor());
            var result = await manager.StartServiceAsync(serviceId, CancellationToken.None);

            result.Success.Should().BeTrue();
            await WaitForProcessExitAsync(manager, serviceId);
            var log = await ReadRunLogAsync(result.RunId!.Value);
            log.Should().Contain("AzureWebJobsStorage=UseDevelopmentStorage=true;CustomEndpoint=http://localhost:11000");
            log.Should().NotContain("AzureWebJobsStorage=UseDevelopmentStorage=true,");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StopAll_stops_orphaned_run_processes_recorded_in_history()
    {
        var process = StartLiveProcess();
        try
        {
            var serviceId = await SeedRunnableServiceAsync(
                "orphaned-" + Guid.NewGuid().ToString("N"),
                AppContext.BaseDirectory,
                "Custom");
            var runId = await SeedActiveRunAsync(serviceId, process.Id, DateTimeOffset.UtcNow);
            var manager = CreateManager();

            var result = await manager.StopAllAsync(CancellationToken.None);

            result.Stopped.Should().Be(1);
            result.Outcomes.Should().ContainSingle(o => o.ServiceId == serviceId && o.Success);
            process.HasExited.Should().BeTrue();

            await using var db = _factory.CreateDbContext();
            var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
            run.Status.Should().Be(ProcessStatusNames.Killed);
            run.StoppedUtc.Should().NotBeNull();
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best effort cleanup
            }

            process.Dispose();
        }
    }

    private async Task<int> SeedServiceAsync(string name, int displayOrder, bool enabled)
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = name,
            ServiceType = "Custom",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "devdeck-missing-" + Guid.NewGuid().ToString("N")),
            StartCommand = "dotnet",
            Enabled = enabled,
            DisplayOrder = displayOrder,
        };
        db.DevServices.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private async Task<int> SeedRunnableServiceAsync(
        string name,
        string workingDirectory,
        string serviceType,
        params ServiceEnvironmentVariable[] environmentVariables)
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = name,
            ServiceType = serviceType,
            WorkingDirectory = workingDirectory,
            StartCommand = "dotnet",
            StartArguments = "--version",
            Enabled = true,
        };
        service.EnvironmentVariables.AddRange(environmentVariables);
        db.DevServices.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private async Task<long> SeedActiveRunAsync(int serviceId, int processId, DateTimeOffset startedUtc)
    {
        await using var db = _factory.CreateDbContext();
        var run = new ServiceRun
        {
            DevServiceId = serviceId,
            StartedUtc = startedUtc,
            ProcessId = processId,
            Status = ProcessStatusNames.Running,
        };
        db.ServiceRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static Process StartLiveProcess()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c pause")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        return Process.Start(psi)!;
    }

    private async Task<string> ReadRunLogAsync(long runId)
    {
        await using var db = _factory.CreateDbContext();
        var run = await db.ServiceRuns.SingleAsync(r => r.Id == runId);
        run.LogFilePath.Should().NotBeNull();
        await using var stream = new FileStream(run.LogFilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task WaitForProcessExitAsync(DevDeckProcessManager manager, int serviceId)
    {
        var process = manager.GetRunningProcess(serviceId)?.Process;
        if (process is null)
        {
            return;
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private DevDeckProcessManager CreateManager(StubAzuriteSupervisor? azurite = null)
    {
        var options = new DevDeckOptions { DevelopmentOnly = false }; // allow execution outside Development
        return new DevDeckProcessManager(
            _factory,
            new ProcessLogBuffer(5000, 1000),
            new LogFileWriter(),
            new CommandTemplateRenderer(),
            new CommandExecutableResolver(),
            new TestOptionsMonitor<DevDeckOptions>(options),
            new TestWebHostEnvironment(),
            new HealthStatusCache(),
            azurite ?? new StubAzuriteSupervisor(),
            NullLogger<DevDeckProcessManager>.Instance);
    }

    private sealed class StubAzuriteSupervisor : IAzuriteSupervisor
    {
        public int Calls { get; private set; }

        public Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken)
        {
            Calls++;
            log("Azurite test stub is ready.");
            return Task.FromResult(new AzuriteReadyResult(true));
        }
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
        public TestDbContextFactory(SqliteConnection connection) =>
            _options = new DbContextOptionsBuilder<DevDeckDbContext>().UseSqlite(connection).Options;
        public DevDeckDbContext CreateDbContext() => new(_options);
    }
}
