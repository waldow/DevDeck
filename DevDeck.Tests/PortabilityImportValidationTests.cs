using System.Text.Json;
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

    [Theory]
    [InlineData("/{**path}")]
    [InlineData("/{x}/{**rest}")]
    public async Task Catch_all_route_with_any_parameter_name_is_skipped(string matchPath)
    {
        var result = await ImportRouteAsync(matchPath: matchPath, destination: "http://localhost:7071/");

        result.Created.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Catch-all");
    }

    [Fact]
    public async Task Route_with_malformed_match_path_is_skipped()
    {
        var result = await ImportRouteAsync(matchPath: "/api/{**rest", destination: "http://localhost:7071/");

        result.Created.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().Contain("not a valid route template");
    }

    [Fact]
    public async Task Services_import_tolerates_names_that_differ_only_in_case()
    {
        // Both rows are legal (the unique index is case-sensitive); building a case-insensitive
        // dictionary over them used to throw and turn the import into a 500.
        await SeedServiceAsync("API", url: null);
        await SeedServiceAsync("api", url: null);

        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(new PortableService
        {
            Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm", Port = 4000,
        }));

        result.Errors.Should().BeEmpty();
        result.Updated.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync(s => s.Name == "api")).Port.Should().Be(4000);
        (await db.DevServices.SingleAsync(s => s.Name == "API")).Port.Should().Be(3001);
    }

    [Fact]
    public async Task Services_import_still_matches_case_insensitively_when_unambiguous()
    {
        await SeedServiceAsync("Api", url: null);

        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(new PortableService
        {
            Name = "API", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm", Port = 4000,
        }));

        result.Updated.Should().Be(1);
        result.Created.Should().Be(0);
    }

    [Fact]
    public async Task Health_checks_with_repeated_urls_are_merged_one_to_one()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var service = new DevService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm" };
            service.HealthChecks.Add(new ServiceHealthCheck { Url = "http://localhost:3001/health", IntervalSeconds = 5 });
            service.HealthChecks.Add(new ServiceHealthCheck { Url = "http://localhost:3001/health", IntervalSeconds = 5 });
            db.DevServices.Add(service);
            await db.SaveChangesAsync();
        }

        var incoming = new PortableService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm" };
        incoming.HealthChecks.Add(new PortableHealthCheck { Url = "http://localhost:3001/health", IntervalSeconds = 30 });
        incoming.HealthChecks.Add(new PortableHealthCheck { Url = "http://localhost:3001/health", IntervalSeconds = 60 });
        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(incoming));

        result.Errors.Should().BeEmpty();
        await using var verify = _factory.CreateDbContext();
        (await verify.ServiceHealthChecks.Select(h => h.IntervalSeconds).ToListAsync())
            .Should().BeEquivalentTo(new[] { 30, 60 });
    }

    [Fact]
    public async Task Env_var_repeated_in_the_file_is_stored_once()
    {
        var incoming = new PortableService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm" };
        incoming.EnvironmentVariables.Add(new PortableEnvVar { Key = "PORT", Value = "1" });
        incoming.EnvironmentVariables.Add(new PortableEnvVar { Key = "PORT", Value = "2" });

        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(incoming));

        result.Errors.Should().BeEmpty();
        result.Created.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        (await db.ServiceEnvironmentVariables.SingleAsync()).Value.Should().Be("2");
    }

    [Fact]
    public async Task Profiles_import_tolerates_duplicate_profile_names()
    {
        // Profile names have no unique index at all.
        await using (var db = _factory.CreateDbContext())
        {
            db.LaunchProfiles.Add(new LaunchProfile { Name = "Dev" });
            db.LaunchProfiles.Add(new LaunchProfile { Name = "Dev" });
            await db.SaveChangesAsync();
        }
        var json = JsonSerializer.Serialize(
            new PortableProfileBundle { Profiles = [new PortableProfile { Name = "Dev", Description = "updated" }] },
            PortabilityJson.Options);

        var result = await new PortabilityImporter(_factory).ImportProfilesAsync(json);

        result.Errors.Should().BeEmpty();
        result.Updated.Should().Be(1);
    }

    private static string ServicesJson(params PortableService[] services) =>
        JsonSerializer.Serialize(new PortableServiceBundle { Services = services.ToList() }, PortabilityJson.Options);

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
