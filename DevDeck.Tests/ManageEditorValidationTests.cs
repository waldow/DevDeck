using System.Diagnostics;
using DevDeck.Web.Areas.Manage.Controllers;
using DevDeck.Web.Areas.Manage.ViewModels;
using DevDeck.Web.Data;
using DevDeck.Web.Data.Entities;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Portability;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yarp.ReverseProxy.Configuration;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DevDeck.Tests;

// The service and route editors must turn bad input into validation messages, not into
// DbUpdateExceptions (500s) or states the UI can't get out of.
public sealed class ManageEditorValidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _factory;
    private readonly FakeProcessManager _manager = new();

    public ManageEditorValidationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Service_edit_refuses_switching_a_running_service_to_passthru()
    {
        var serviceId = await SeedServiceAsync();
        _manager.Running.Add(serviceId);
        var model = ServiceModel();
        model.UseExternalInstance = true;

        var result = await ServicesController().Edit(serviceId, model);

        result.Should().BeOfType<ViewResult>();
        var controllerState = ((ViewResult)result).ViewData.ModelState;
        controllerState[nameof(ServiceEditViewModel.UseExternalInstance)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("Stop the service");
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync()).UseExternalInstance.Should().BeFalse();
    }

    [Fact]
    public async Task Service_edit_allows_switching_a_stopped_service_to_passthru()
    {
        var serviceId = await SeedServiceAsync();
        var model = ServiceModel();
        model.UseExternalInstance = true;

        var result = await ServicesController().Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync()).UseExternalInstance.Should().BeTrue();
    }

    [Fact]
    public async Task Service_create_reports_a_repeated_environment_variable_key()
    {
        var model = ServiceModel();
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "PORT", Value = "1" });
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "PORT", Value = "2" });

        var result = await ServicesController().Create(model);

        result.Should().BeOfType<ViewResult>();
        ((ViewResult)result).ViewData.ModelState[nameof(ServiceEditViewModel.EnvironmentVariables)]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("'PORT'");
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Service_create_ignores_a_repeated_key_on_a_deleted_row()
    {
        var model = ServiceModel();
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "PORT", Value = "1" });
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "PORT", Value = "2", Delete = true });

        var result = await ServicesController().Create(model);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task Route_create_reports_a_duplicate_name()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.ProxyRoutes.Add(new ProxyRoute { Name = "api", MatchPath = "/api/{**rest}", PathTransformMode = "None", DestinationUrlOverride = "http://localhost:3001/" });
            await db.SaveChangesAsync();
        }

        var result = await RoutesController().Create(RouteModel("api", "/api2/{**rest}"));

        result.Should().BeOfType<ViewResult>();
        ((ViewResult)result).ViewData.ModelState[nameof(ProxyRouteEditViewModel.Name)]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task Route_create_reports_what_yarp_would_reject()
    {
        var model = RouteModel("locked", "/locked/{**rest}");
        model.AuthorizationPolicy = "no-such-policy";

        var result = await RoutesController().Create(model);

        result.Should().BeOfType<ViewResult>();
        ((ViewResult)result).ViewData.ModelState[string.Empty]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("no-such-policy");
        await using var db = _factory.CreateDbContext();
        (await db.ProxyRoutes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Route_create_saves_a_valid_route()
    {
        var result = await RoutesController().Create(RouteModel("app", "/app/{**rest}"));

        result.Should().BeOfType<RedirectToActionResult>();
        await using var db = _factory.CreateDbContext();
        (await db.ProxyRoutes.SingleAsync()).Name.Should().Be("app");
    }

    private ServicesController ServicesController()
    {
        var options = MsOptions.Create(new DevDeckOptions());
        return new ServicesController(
            _factory,
            _manager,
            new CommandPresetProvider(new CommandExecutableResolver()),
            new PortProbeService(_manager),
            new PortabilityExporter(_factory),
            new PortabilityImporter(_factory),
            new DevDeckProxyConfigProvider(_factory, Builder(), NullLogger<DevDeckProxyConfigProvider>.Instance),
            options);
    }

    private ProxyRoutesController RoutesController()
    {
        var builder = Builder();
        return new ProxyRoutesController(
            _factory,
            new DevDeckProxyConfigProvider(_factory, builder, NullLogger<DevDeckProxyConfigProvider>.Instance),
            new ProxyDestinationValidator(allowExternal: false),
            builder,
            new CommandTemplateRenderer(),
            new PortProbeService(_manager),
            _manager,
            MsOptions.Create(new DevDeckOptions()),
            new PortabilityExporter(_factory),
            new PortabilityImporter(_factory));
    }

    private static ProxyRouteBuilder Builder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddReverseProxy();
        var yarpValidator = services.BuildServiceProvider().GetRequiredService<IConfigValidator>();
        return new ProxyRouteBuilder(new ProxyDestinationValidator(allowExternal: false), yarpValidator: yarpValidator);
    }

    private async Task<int> SeedServiceAsync()
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = "api",
            ServiceType = "NodeApi",
            WorkingDirectory = AppContext.BaseDirectory,
            StartCommand = "npm",
            Port = 3001,
        };
        db.DevServices.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private static ServiceEditViewModel ServiceModel() => new()
    {
        Name = "api",
        ServiceType = "NodeApi",
        WorkingDirectory = AppContext.BaseDirectory,
        StartCommand = "npm",
        Port = 3001,
    };

    private static ProxyRouteEditViewModel RouteModel(string name, string matchPath) => new()
    {
        Name = name,
        MatchPath = matchPath,
        PathTransformMode = "None",
        DestinationUrlOverride = "http://localhost:3001/",
    };

    private sealed class FakeProcessManager : IDevDeckProcessManager
    {
        public HashSet<int> Running { get; } = new();

        public RunningProcessInfo? GetRunningProcess(int serviceId) => Running.Contains(serviceId)
            ? new RunningProcessInfo
            {
                DevServiceId = serviceId,
                ServiceRunId = 1,
                ServiceName = "svc",
                Process = new Process(),
                StartedUtc = DateTimeOffset.UtcNow,
                LogFilePath = "run.log",
                Status = ProcessStatus.Running,
            }
            : null;

        public IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses() =>
            Running.Select(id => GetRunningProcess(id)!).ToList();

        public Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StopServiceResult { ServiceId = serviceId, Success = false });

        public Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new RestartServiceResult { ServiceId = serviceId, Success = false });

        public Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken) =>
            Task.FromResult(new StartProfileResult { ProfileId = profileId, Success = false });

        public Task<StartAllResult> StartAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartAllResult());

        public Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StopAllResult());

        public IReadOnlyList<LogLine> GetLiveLogs(int serviceId) => [];

        public LiveLogSlice GetLiveLogsSince(int serviceId, long since) => new([], 0, 0, false);

        public bool IsServiceBusy(int serviceId) => false;

        public void ClearLiveLogs(int serviceId)
        {
        }

        public void AppendProxyLog(RunningProcessInfo info, string text)
        {
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DevDeckDbContext>
    {
        private readonly DbContextOptions<DevDeckDbContext> _options;
        public TestDbContextFactory(SqliteConnection connection) =>
            _options = new DbContextOptionsBuilder<DevDeckDbContext>().UseSqlite(connection).Options;
        public DevDeckDbContext CreateDbContext() => new(_options);
    }
}
