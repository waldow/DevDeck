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
    public async Task A_health_check_url_differing_only_in_case_takes_the_files_casing()
    {
        await using (var db = _factory.CreateDbContext())
        {
            var service = new DevService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm" };
            service.HealthChecks.Add(new ServiceHealthCheck { Url = "http://localhost:3001/Health" });
            db.DevServices.Add(service);
            await db.SaveChangesAsync();
        }

        var incoming = new PortableService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm" };
        incoming.HealthChecks.Add(new PortableHealthCheck { Url = "http://localhost:3001/health" });
        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(incoming));

        result.Errors.Should().BeEmpty();
        await using var verify = _factory.CreateDbContext();
        (await verify.ServiceHealthChecks.Select(h => h.Url).SingleAsync()).Should().Be("http://localhost:3001/health");
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

    [Fact]
    public async Task Services_in_one_file_whose_names_differ_only_by_case_stay_separate()
    {
        // Regression: the case-insensitive fallback matched the row created moments earlier for
        // the other entry, merging both services (secrets included) into one row.
        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(
            new PortableService { Name = "API", ServiceType = "NodeApi", WorkingDirectory = "/tmp/upper", StartCommand = "npm", Port = 1111 },
            new PortableService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp/lower", StartCommand = "npm", Port = 2222 }));

        result.Created.Should().Be(2);
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync(s => s.Name == "API")).Port.Should().Be(1111);
        (await db.DevServices.SingleAsync(s => s.Name == "api")).Port.Should().Be(2222);
    }

    [Fact]
    public async Task A_case_insensitive_match_is_not_used_when_the_file_names_the_exact_row_too()
    {
        await SeedServiceAsync("Api", url: null);

        var result = await new PortabilityImporter(_factory).ImportServicesAsync(ServicesJson(
            new PortableService { Name = "API", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm", Port = 1111 },
            new PortableService { Name = "Api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm", Port = 2222 }));

        result.Created.Should().Be(1);
        result.Updated.Should().Be(1);
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync(s => s.Name == "Api")).Port.Should().Be(2222);
        (await db.DevServices.SingleAsync(s => s.Name == "API")).Port.Should().Be(1111);
    }

    [Fact]
    public async Task A_profile_listing_a_service_twice_imports_it_once()
    {
        await SeedServiceAsync("api", url: null);
        var json = """
        { "schemaVersion": 1, "profiles": [ { "name": "Dev", "services": [
            { "serviceName": "api", "startOrder": 1 }, { "serviceName": "api", "startOrder": 2 } ] } ] }
        """;

        var result = await new PortabilityImporter(_factory).ImportProfilesAsync(json);

        result.Errors.Should().BeEmpty();
        await using var db = _factory.CreateDbContext();
        (await db.LaunchProfileServices.SingleAsync()).StartOrder.Should().Be(2);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "services": null }""", "services")]
    [InlineData("""{ "schemaVersion": 1, "services": [ null ] }""", "services")]
    [InlineData("""{ "schemaVersion": 1, "services": [ { "name": "a", "serviceType": "NodeApi", "workingDirectory": "/tmp", "startCommand": "npm", "environmentVariables": null, "healthChecks": [ null ] } ] }""", "services")]
    [InlineData("""{ "schemaVersion": 1, "profiles": [ { "name": "p", "services": null } ] }""", "profiles")]
    [InlineData("""{ "schemaVersion": 1, "routes": [ null ] }""", "routes")]
    [InlineData("null", "routes")]
    public async Task Explicit_nulls_are_reported_not_thrown(string json, string kind)
    {
        var importer = new PortabilityImporter(_factory);

        var act = kind switch
        {
            "services" => importer.ImportServicesAsync(json),
            "profiles" => importer.ImportProfilesAsync(json),
            _ => importer.ImportRoutesAsync(json),
        };

        await act; // must not throw
    }

    [Theory]
    [InlineData("\"port\": 70000")]
    [InlineData("\"externalPort\": 0")]
    [InlineData("\"displayOrder\": -1")]
    [InlineData("\"healthChecks\": [ { \"url\": \"http://localhost:1/\", \"intervalSeconds\": 0 } ]")]
    [InlineData("\"healthChecks\": [ { \"url\": \"http://localhost:1/\", \"expectedStatusCode\": 0 } ]")]
    [InlineData("\"environmentVariables\": [ { \"key\": \"A=B\", \"value\": \"x\" } ]")]
    public async Task Services_with_values_the_editor_refuses_are_skipped(string field)
    {
        var json = $$"""
        { "schemaVersion": 1, "services": [ { "name": "api", "serviceType": "NodeApi", "workingDirectory": "/tmp", "startCommand": "npm", {{field}} } ] }
        """;

        var result = await new PortabilityImporter(_factory).ImportServicesAsync(json);

        result.Created.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Skipped service 'api'");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(900)]
    public async Task Routes_with_a_timeout_the_editor_refuses_are_skipped(int timeout)
    {
        var json = $$"""
        { "schemaVersion": 1, "routes": [ { "name": "r", "matchPath": "/r/{**rest}", "destinationUrlOverride": "http://localhost:3001/", "timeoutSeconds": {{timeout}} } ] }
        """;

        var result = await new PortabilityImporter(_factory).ImportRoutesAsync(json);

        result.Created.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Timeout");
    }

    [Fact]
    public async Task A_route_routing_could_not_tell_apart_from_another_is_skipped()
    {
        var json = """
        { "schemaVersion": 1, "routes": [
            { "name": "a", "matchPath": "/api/{**rest}", "destinationUrlOverride": "http://localhost:3001/" },
            { "name": "b", "matchPath": "/api/{**path}", "destinationUrlOverride": "http://localhost:3002/" } ] }
        """;

        var result = await new PortabilityImporter(_factory).ImportRoutesAsync(json);

        result.Created.Should().Be(1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("Skipped route 'b'").And.Contain("'a'");
    }

    [Fact]
    public async Task Reimporting_a_route_does_not_conflict_with_itself()
    {
        var json = """
        { "schemaVersion": 1, "routes": [ { "name": "a", "matchPath": "/api/{**rest}", "destinationUrlOverride": "http://localhost:3001/" } ] }
        """;
        var importer = new PortabilityImporter(_factory);
        await importer.ImportRoutesAsync(json);

        var result = await importer.ImportRoutesAsync(json);

        result.Errors.Should().BeEmpty();
        result.Updated.Should().Be(1);
    }

    [Fact]
    public void Messages_kept_for_the_next_page_are_capped()
    {
        // They travel in a cookie; uncapped, a large import pushed every later request past the
        // server's header limit (HTTP 431) until the cookies were cleared.
        var result = new PortabilityImportResult();
        for (var i = 0; i < 200; i++)
        {
            result.Errors.Add($"Skipped route 'route-{i}': " + new string('x', 5000));
        }

        var json = result.ToTempDataMessages();
        var messages = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json)!;

        json.Length.Should().BeLessThan(10_000);
        messages.Should().HaveCount(21);
        messages[^1].Should().Contain("180 more");
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
