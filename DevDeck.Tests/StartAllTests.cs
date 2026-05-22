using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.Data.Sqlite;
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

    private DevDeckProcessManager CreateManager()
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
            NullLogger<DevDeckProcessManager>.Instance);
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
