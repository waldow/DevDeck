using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Portability;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Tests;

// Route imports must apply the same safety checks as the route editor — otherwise an
// imported bundle can plant reserved-path or external-destination rows in the database.
public sealed class PortabilityImportValidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public PortabilityImportValidationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("/Manage/{**catch-all}")]
    [InlineData("/manage/services")]
    [InlineData("/css/site.css")]
    public async Task Route_with_reserved_match_path_is_skipped_with_error(string matchPath)
    {
        var result = await ImportRouteAsync(matchPath: matchPath, destination: "http://localhost:7071/");

        result.Created.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("reserved");
        (await CountRoutesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Catch_all_route_is_skipped_when_catch_all_is_disabled()
    {
        var result = await ImportRouteAsync(matchPath: "/{**catch-all}", destination: "http://localhost:7071/");

        result.Created.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Catch-all");
        (await CountRoutesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Route_with_external_destination_is_skipped_with_error()
    {
        var result = await ImportRouteAsync(matchPath: "/api/{**catch-all}", destination: "http://evil.example.com/");

        result.Created.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("AllowExternalDestinations");
        (await CountRoutesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Route_with_localhost_destination_imports_normally()
    {
        var result = await ImportRouteAsync(matchPath: "/api/{**catch-all}", destination: "http://localhost:7071/");

        result.Created.Should().Be(1);
        result.Errors.Should().BeEmpty();
        (await CountRoutesAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Templated_override_is_rendered_against_linked_service_before_validation()
    {
        await SeedServiceAsync("api", url: null);

        var result = await ImportRouteAsync(matchPath: "/api/{**catch-all}", destination: "http://localhost:{port}/api", serviceName: "api");

        result.Errors.Should().BeEmpty();
        result.Created.Should().Be(1);
    }

    [Fact]
    public async Task Linked_service_with_external_url_is_skipped_when_no_override()
    {
        await SeedServiceAsync("remote", url: "http://evil.example.com/");

        var result = await ImportRouteAsync(matchPath: "/api/{**catch-all}", destination: null, serviceName: "remote");

        result.Created.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().Contain("AllowExternalDestinations");
    }

    private async Task SeedServiceAsync(string name, string? url)
    {
        await using var db = _factory.CreateDbContext();
        db.DevServices.Add(new DevService
        {
            Name = name,
            ServiceType = "NodeApi",
            WorkingDirectory = "/tmp",
            StartCommand = "npm",
            Port = 3001,
            Url = url,
        });
        await db.SaveChangesAsync();
    }

    private async Task<PortabilityImportResult> ImportRouteAsync(string matchPath, string? destination, string? serviceName = null)
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "routes": [{
            "name": "Imported",
            "enabled": true,
            "serviceName": {{(serviceName is null ? "null" : $"\"{serviceName}\"")}},
            "destinationUrlOverride": {{(destination is null ? "null" : $"\"{destination}\"")}},
            "matchPath": "{{matchPath}}",
            "pathTransformMode": "None"
          }]
        }
        """;
        var importer = new PortabilityImporter(_factory);
        return await importer.ImportRoutesAsync(json);
    }

    private async Task<int> CountRoutesAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.ProxyRoutes.CountAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DevDeckDbContext>
    {
        private readonly DbContextOptions<DevDeckDbContext> _options;
        public TestDbContextFactory(SqliteConnection connection)
        {
            _options = new DbContextOptionsBuilder<DevDeckDbContext>().UseSqlite(connection).Options;
        }
        public DevDeckDbContext CreateDbContext() => new(_options);
    }
}
