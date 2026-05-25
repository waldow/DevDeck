using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDeck.Tests;

public sealed class ProxyRequestLoggerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public ProxyRequestLoggerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void FormatInbound_renders_client_verb_and_path_with_arrow()
    {
        ProxyRequestLogger.FormatInbound("127.0.0.1", "GET", "/api/users?id=1")
            .Should().Be("127.0.0.1 --> GET /api/users?id=1");
    }

    [Fact]
    public void FormatOutbound_renders_status_destination_duration_and_size()
    {
        ProxyRequestLogger.FormatOutbound("127.0.0.1", "GET", "/api/users", "http://localhost:5173", 200, 42, 1234)
            .Should().Be("127.0.0.1 <-- 200 GET /api/users -> http://localhost:5173 42ms 1.2 KB");
    }

    [Fact]
    public void FormatOutbound_renders_dash_when_response_size_unknown()
    {
        ProxyRequestLogger.FormatOutbound("-", "POST", "/upload", "-", 204, 7, null)
            .Should().Be("- <-- 204 POST /upload -> - 7ms -");
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    public void FormatSize_renders_human_readable_bytes(long? bytes, string expected)
    {
        ProxyRequestLogger.FormatSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void AppendProxyLog_is_a_no_op_when_the_service_is_not_running()
    {
        var manager = CreateManager();

        // Service 999 is not running, so there is no run/log file to attach the line to.
        manager.AppendProxyLog(999, "127.0.0.1 --> GET /");

        manager.GetLiveLogs(999).Should().BeEmpty();
    }

    private DevDeckProcessManager CreateManager()
    {
        var options = new DevDeckOptions { DevelopmentOnly = false };
        return new DevDeckProcessManager(
            _factory,
            new Web.Services.Runtime.ProcessLogBuffer(5000, 1000),
            new Web.Services.Logs.LogFileWriter(),
            new CommandTemplateRenderer(),
            new CommandExecutableResolver(),
            new TestOptionsMonitor<DevDeckOptions>(options),
            new TestWebHostEnvironment(),
            new HealthStatusCache(),
            new StubAzuriteSupervisor(),
            NullLogger<DevDeckProcessManager>.Instance);
    }

    private sealed class StubAzuriteSupervisor : IAzuriteSupervisor
    {
        public Task<AzuriteReadyResult> EnsureRunningAsync(Action<string> log, CancellationToken cancellationToken)
            => Task.FromResult(new AzuriteReadyResult(true));
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
