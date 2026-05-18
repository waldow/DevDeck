using System.Text.Json;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Services.Portability;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevDeck.Tests;

public sealed class PortabilityRoundTripTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;

    public PortabilityRoundTripTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Service_round_trip_masks_secret_values_by_default()
    {
        await Seed(db => db.DevServices.Add(NewService("Func", secret: "super-secret")));

        var exporter = new PortabilityExporter(_factory);
        var bundle = await exporter.ExportServicesAsync(includeSecrets: false);

        var secret = bundle.Services[0].EnvironmentVariables.Single(e => e.IsSecret);
        secret.Value.Should().Be(EnvVarEditRow.SecretPlaceholder);
    }

    [Fact]
    public async Task Service_round_trip_includes_secrets_when_opted_in()
    {
        await Seed(db => db.DevServices.Add(NewService("Func", secret: "super-secret")));

        var exporter = new PortabilityExporter(_factory);
        var bundle = await exporter.ExportServicesAsync(includeSecrets: true);

        bundle.Services[0].EnvironmentVariables.Single(e => e.IsSecret).Value.Should().Be("super-secret");
    }

    [Fact]
    public async Task Import_with_masked_secret_preserves_existing_db_value()
    {
        await Seed(db => db.DevServices.Add(NewService("Func", secret: "real-secret")));

        var exporter = new PortabilityExporter(_factory);
        var masked = await exporter.ExportServicesAsync(includeSecrets: false);

        // Round-trip — replay the masked document.
        var json = JsonSerializer.Serialize(masked, PortabilityJson.Options);
        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportServicesAsync(json);

        result.Updated.Should().Be(1);
        result.Created.Should().Be(0);

        await using var verifyDb = _factory.CreateDbContext();
        var stored = await verifyDb.ServiceEnvironmentVariables.SingleAsync(e => e.IsSecret);
        stored.Value.Should().Be("real-secret", because: "the placeholder must not overwrite the stored secret");
    }

    [Fact]
    public async Task Import_updates_scalars_of_an_existing_service()
    {
        await Seed(db => db.DevServices.Add(NewService("Func", port: 7071)));

        var json = """
        {
          "schemaVersion": 1,
          "services": [{
            "name": "Func", "serviceType": "AzureFunctions", "workingDirectory": "C:\\src\\func",
            "startCommand": "func", "port": 9999, "enabled": true,
            "environmentVariables": [], "healthChecks": []
          }]
        }
        """;

        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportServicesAsync(json);

        result.Updated.Should().Be(1);
        await using var verifyDb = _factory.CreateDbContext();
        (await verifyDb.DevServices.SingleAsync()).Port.Should().Be(9999);
    }

    [Fact]
    public async Task Import_creates_missing_service()
    {
        var json = """
        {
          "schemaVersion": 1,
          "services": [{
            "name": "Vite", "serviceType": "Vite", "workingDirectory": "C:\\src\\app",
            "startCommand": "npm", "startArguments": "run dev", "port": 5173,
            "environmentVariables": [], "healthChecks": []
          }]
        }
        """;

        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportServicesAsync(json);

        result.Created.Should().Be(1);
        await using var verifyDb = _factory.CreateDbContext();
        (await verifyDb.DevServices.SingleAsync()).Name.Should().Be("Vite");
    }

    [Fact]
    public async Task Profile_import_warns_when_referenced_service_is_missing()
    {
        var json = """
        {
          "schemaVersion": 1,
          "profiles": [{
            "name": "FullStack", "displayOrder": 0,
            "services": [
              { "serviceName": "Ghost", "startOrder": 0, "startDelaySeconds": 0 }
            ]
          }]
        }
        """;

        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportProfilesAsync(json);

        result.Created.Should().Be(1);
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("Ghost").And.Contain("FullStack");
        await using var verifyDb = _factory.CreateDbContext();
        (await verifyDb.LaunchProfileServices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Route_import_with_missing_service_nulls_FK_and_warns()
    {
        var json = """
        {
          "schemaVersion": 1,
          "routes": [{
            "name": "API", "enabled": true,
            "serviceName": "Ghost",
            "destinationUrlOverride": "http://localhost:7071/",
            "matchPath": "/api/{**catch-all}",
            "pathTransformMode": "None"
          }]
        }
        """;

        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportRoutesAsync(json);

        result.Created.Should().Be(1);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("Ghost");
        await using var verifyDb = _factory.CreateDbContext();
        (await verifyDb.ProxyRoutes.SingleAsync()).DevServiceId.Should().BeNull();
    }

    [Fact]
    public async Task Future_schema_version_is_rejected()
    {
        var json = """{ "schemaVersion": 999, "services": [] }""";

        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportServicesAsync(json);

        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("999").And.Contain(PortabilityJson.CurrentSchemaVersion.ToString());
        result.Created.Should().Be(0);
        result.Updated.Should().Be(0);
    }

    [Fact]
    public async Task Malformed_json_returns_error_not_throws()
    {
        var importer = new PortabilityImporter(_factory);
        var result = await importer.ImportServicesAsync("{ not json");

        result.Errors.Should().NotBeEmpty();
        result.Created.Should().Be(0);
    }

    private async Task Seed(Action<DevDeckDbContext> seed)
    {
        await using var db = _factory.CreateDbContext();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static DevService NewService(string name, int? port = null, string? secret = null) => new()
    {
        Name = name,
        ServiceType = "AzureFunctions",
        WorkingDirectory = "C:\\src\\test",
        StartCommand = "func",
        StartArguments = "start",
        Port = port,
        EnvironmentVariables = secret is null ? [] :
        [
            new ServiceEnvironmentVariable { Key = "API_KEY", Value = secret, IsSecret = true },
            new ServiceEnvironmentVariable { Key = "NORMAL", Value = "ok", IsSecret = false },
        ],
    };

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
