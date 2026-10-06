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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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
    private readonly HealthStatusCache _healthCache = new();
    private DevDeckProxyConfigProvider? _proxyProvider;

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

    [Fact]
    public async Task Service_edit_ignores_field_errors_of_rows_removed_with_the_remove_button()
    {
        // A removed row (Delete=true) is skipped on save, so e.g. an imported interval of 0 on
        // it must not make every save fail.
        var serviceId = await SeedServiceAsync();
        var model = ServiceModel();
        model.HealthChecks.Add(new HealthCheckEditRow { Url = "http://localhost:3001/health", IntervalSeconds = 0, Delete = true });
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "", Value = "" });
        var controller = ServicesController();
        controller.ModelState.AddModelError("HealthChecks[0].IntervalSeconds", "Interval must be between 1 second and 1 day.");
        controller.ModelState.AddModelError("EnvironmentVariables[0].Key", "The Key field is required.");

        var result = await controller.Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task Service_edit_still_reports_field_errors_of_kept_rows()
    {
        var serviceId = await SeedServiceAsync();
        var model = ServiceModel();
        model.HealthChecks.Add(new HealthCheckEditRow { Url = "http://localhost:3001/health", IntervalSeconds = 0 });
        var controller = ServicesController();
        controller.ModelState.AddModelError("HealthChecks[0].IntervalSeconds", "Interval must be between 1 second and 1 day.");

        var result = await controller.Edit(serviceId, model);

        result.Should().BeOfType<ViewResult>();
    }

    [Fact]
    public async Task Service_edit_drops_an_invalid_external_port_while_passthru_is_off()
    {
        // The field is hidden unless passthru is on, so its error could never be fixed.
        var serviceId = await SeedServiceAsync();
        var model = ServiceModel();
        model.ExternalPort = 0;
        var controller = ServicesController();
        controller.ModelState.AddModelError(nameof(ServiceEditViewModel.ExternalPort), "External port must be between 1 and 65535.");

        var result = await controller.Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync()).ExternalPort.Should().BeNull();
    }

    [Fact]
    public async Task Service_edit_can_swap_two_environment_variable_keys()
    {
        // Renaming rows in place collided on the unique (service, key) index mid-save.
        var serviceId = await SeedServiceAsync();
        int aId, bId;
        await using (var db = _factory.CreateDbContext())
        {
            var service = await db.DevServices.Include(s => s.EnvironmentVariables).SingleAsync();
            service.EnvironmentVariables.Add(new ServiceEnvironmentVariable { Key = "A", Value = "1" });
            service.EnvironmentVariables.Add(new ServiceEnvironmentVariable { Key = "B", Value = "2", IsSecret = true });
            await db.SaveChangesAsync();
            aId = service.EnvironmentVariables.Single(e => e.Key == "A").Id;
            bId = service.EnvironmentVariables.Single(e => e.Key == "B").Id;
        }
        var model = ServiceModel();
        model.EnvironmentVariables.Add(new EnvVarEditRow { Id = aId, Key = "B", Value = "1" });
        model.EnvironmentVariables.Add(new EnvVarEditRow { Id = bId, Key = "A", Value = EnvVarEditRow.SecretPlaceholder, IsSecret = true });

        var result = await ServicesController().Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
        await using var verify = _factory.CreateDbContext();
        var env = await verify.ServiceEnvironmentVariables.ToDictionaryAsync(e => e.Key, e => e.Value);
        env.Should().BeEquivalentTo(new Dictionary<string, string> { ["B"] = "1", ["A"] = "2" });
    }

    [Fact]
    public async Task Service_edit_removes_a_saved_environment_variable_whose_key_was_cleared()
    {
        // Blank rows are skipped on save; a saved one used to be kept unchanged — and re-adding
        // its key in a new row then failed the save on the unique (service, key) index.
        var serviceId = await SeedServiceAsync();
        int fooId;
        await using (var db = _factory.CreateDbContext())
        {
            var service = await db.DevServices.Include(s => s.EnvironmentVariables).SingleAsync();
            service.EnvironmentVariables.Add(new ServiceEnvironmentVariable { Key = "FOO", Value = "old" });
            await db.SaveChangesAsync();
            fooId = service.EnvironmentVariables.Single().Id;
        }
        var model = ServiceModel();
        model.EnvironmentVariables.Add(new EnvVarEditRow { Id = fooId, Key = "", Value = "old" });
        model.EnvironmentVariables.Add(new EnvVarEditRow { Key = "FOO", Value = "bar" });

        var result = await ServicesController().Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
        await using var verify = _factory.CreateDbContext();
        var env = await verify.ServiceEnvironmentVariables.ToDictionaryAsync(e => e.Key, e => e.Value);
        env.Should().BeEquivalentTo(new Dictionary<string, string> { ["FOO"] = "bar" });
    }

    [Fact]
    public async Task Service_edit_removes_a_saved_health_check_whose_url_was_cleared()
    {
        var serviceId = await SeedServiceAsync();
        var checkId = await SeedHealthCheckAsync(serviceId, "http://localhost:{port}/health");
        var model = ServiceModel();
        model.HealthChecks.Add(new HealthCheckEditRow { Id = checkId, Url = "" });

        var result = await ServicesController().Edit(serviceId, model);

        result.Should().BeOfType<RedirectToActionResult>();
        await using var verify = _factory.CreateDbContext();
        (await verify.ServiceHealthChecks.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Service_edit_forgets_the_cached_results_of_a_check_whose_url_changed()
    {
        var serviceId = await SeedServiceAsync();
        var checkId = await SeedHealthCheckAsync(serviceId, "http://localhost:{port}/helth");
        _healthCache.Set(serviceId, checkId, HealthStatusNames.Unhealthy, DateTimeOffset.UtcNow.AddSeconds(-1));
        var model = ServiceModel();
        model.HealthChecks.Add(new HealthCheckEditRow { Id = checkId, Url = "http://localhost:{port}/health" });

        await ServicesController().Edit(serviceId, model);

        _healthCache.TryGetResult(serviceId, checkId, out _).Should().BeFalse("a stale Unhealthy would keep the gate shut for a whole interval");
    }

    [Fact]
    public async Task Service_edit_keeps_cached_results_when_no_check_target_changed()
    {
        var serviceId = await SeedServiceAsync();
        var checkId = await SeedHealthCheckAsync(serviceId, "http://localhost:{port}/health");
        _healthCache.Set(serviceId, checkId, HealthStatusNames.Healthy, DateTimeOffset.UtcNow.AddSeconds(-1));
        var model = ServiceModel();
        model.DisplayOrder = 3;
        model.HealthChecks.Add(new HealthCheckEditRow { Id = checkId, Url = "http://localhost:{port}/health" });

        await ServicesController().Edit(serviceId, model);

        _healthCache.TryGetResult(serviceId, checkId, out var result).Should().BeTrue();
        result.Status.Should().Be(HealthStatusNames.Healthy);
    }

    [Theory]
    [InlineData("/Manage/Services", true)]
    [InlineData("/Manage/Services?x=1", true)]
    [InlineData("//evil.example/x", false)]
    [InlineData("/\\evil.example/x", false)]
    [InlineData("evil", false)]
    public void Only_a_same_site_referer_path_is_redirected_back_to(string path, bool local)
    {
        DevDeck.Web.Areas.Manage.Controllers.ServicesController.IsLocalPath(path).Should().Be(local);
    }

    [Fact]
    public async Task Status_snapshot_reports_a_start_in_progress_as_starting()
    {
        // Not yet tracked as running (an Azure Functions start waiting for Azurite): it used to
        // read Stopped, and the page offered Start again.
        var serviceId = await SeedServiceAsync();
        _manager.Starting.Add(serviceId);
        var controller = new StatusController(_factory, _manager, new PortProbeService(_manager), _healthCache);

        var result = await controller.Snapshot(CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Should().BeOfType<JsonResult>().Which.Value);
        json.Should().Contain("\"runtimeStatus\":\"Starting\"");
    }

    [Fact]
    public async Task Profile_stop_reports_the_members_that_failed_to_stop()
    {
        var serviceId = await SeedServiceAsync();
        int profileId;
        await using (var db = _factory.CreateDbContext())
        {
            var profile = new LaunchProfile { Name = "stack" };
            profile.Services.Add(new LaunchProfileService { DevServiceId = serviceId });
            db.LaunchProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }
        var controller = ProfilesController();

        await controller.Stop(profileId, CancellationToken.None);

        controller.TempData["Error"].Should().BeOfType<string>().Which.Should().Contain("api");
    }

    [Fact]
    public async Task Service_delete_rebuilds_the_proxy_snapshot()
    {
        var serviceId = await SeedServiceAsync(url: "http://localhost:{port}");
        await using (var db = _factory.CreateDbContext())
        {
            db.ProxyRoutes.Add(new ProxyRoute { Name = "api", MatchPath = "/api/{**rest}", PathTransformMode = "None", DevServiceId = serviceId });
            await db.SaveChangesAsync();
        }
        var controller = ServicesController();
        await _proxyProvider!.ReloadAsync();
        _proxyProvider.GetConfig().Routes.Should().ContainSingle();

        await controller.Delete(serviceId);

        // Unlinked from the deleted service it has no destination, so it is no longer live
        // (it kept the deleted service's destination and gating until some later reload).
        _proxyProvider.GetConfig().Routes.Should().BeEmpty();
    }

    [Fact]
    public async Task Service_delete_is_refused_while_a_start_is_in_flight()
    {
        var serviceId = await SeedServiceAsync();
        _manager.Busy.Add(serviceId);

        await ServicesController().Delete(serviceId);

        await using var db = _factory.CreateDbContext();
        (await db.DevServices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Route_create_refuses_a_route_routing_could_not_tell_apart_from_another()
    {
        await RoutesController().Create(RouteModel("api", "/api/{**rest}"));

        var result = await RoutesController().Create(RouteModel("api-v2", "/api/{**anything}"));

        result.Should().BeOfType<ViewResult>();
        ((ViewResult)result).ViewData.ModelState[nameof(ProxyRouteEditViewModel.MatchPath)]!.Errors
            .Should().ContainSingle().Which.ErrorMessage.Should().Contain("'api'");
    }

    [Fact]
    public async Task Route_create_allows_the_same_path_with_a_different_order()
    {
        await RoutesController().Create(RouteModel("api", "/api/{**rest}"));
        var model = RouteModel("api-fallback", "/api/{**rest}");
        model.Order = 10;

        var result = await RoutesController().Create(model);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task Route_enable_refuses_a_route_routing_could_not_tell_apart_from_another()
    {
        await RoutesController().Create(RouteModel("api", "/api/{**rest}"));
        var disabled = RouteModel("api-copy", "/api/{**rest}");
        disabled.Enabled = false;
        await RoutesController().Create(disabled);
        int copyId;
        await using (var db = _factory.CreateDbContext())
        {
            copyId = (await db.ProxyRoutes.SingleAsync(r => r.Name == "api-copy")).Id;
        }

        var controller = RoutesController();
        await controller.Enable(copyId);

        controller.TempData["Error"].Should().BeOfType<string>().Which.Should().Contain("was not enabled");
        await using var verify = _factory.CreateDbContext();
        (await verify.ProxyRoutes.SingleAsync(r => r.Id == copyId)).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Profile_edit_ignores_errors_on_rows_that_are_not_included()
    {
        var serviceId = await SeedServiceAsync();
        int profileId;
        await using (var db = _factory.CreateDbContext())
        {
            var profile = new LaunchProfile { Name = "stack" };
            db.LaunchProfiles.Add(profile);
            await db.SaveChangesAsync();
            profileId = profile.Id;
        }
        var model = new ProfileEditViewModel
        {
            Id = profileId,
            Name = "stack",
            Services = [new ProfileServiceRow { DevServiceId = serviceId, ServiceName = "api", Include = false, StartDelaySeconds = 5000 }],
        };
        var controller = ProfilesController();
        controller.ModelState.AddModelError("Services[0].StartDelaySeconds", "Start delay must be between 0 and 3600 seconds.");

        var result = await controller.Edit(profileId, model);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [Fact]
    public async Task Services_import_does_not_switch_a_running_service_to_passthru()
    {
        var serviceId = await SeedServiceAsync();
        _manager.Running.Add(serviceId);
        var json = System.Text.Json.JsonSerializer.Serialize(new PortableServiceBundle
        {
            Services = [new PortableService { Name = "api", ServiceType = "NodeApi", WorkingDirectory = "/tmp", StartCommand = "npm", UseExternalInstance = true }],
        }, PortabilityJson.Options);

        var result = await new PortabilityImporter(_factory, _manager).ImportServicesAsync(json);

        result.Updated.Should().Be(1);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("is running");
        await using var db = _factory.CreateDbContext();
        (await db.DevServices.SingleAsync()).UseExternalInstance.Should().BeFalse();
    }

    [Fact]
    public async Task Downloading_a_log_that_no_longer_exists_explains_why()
    {
        var serviceId = await SeedServiceAsync();
        long runId;
        await using (var db = _factory.CreateDbContext())
        {
            var run = new ServiceRun
            {
                DevServiceId = serviceId,
                Status = "Stopped",
                StartedUtc = DateTimeOffset.UtcNow.AddDays(-30),
                StoppedUtc = DateTimeOffset.UtcNow.AddDays(-30),
                LogFilePath = Path.Combine(DevDeckPaths.LogsFolder, $"gone-{Guid.NewGuid():N}.log"),
            };
            db.ServiceRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }
        var controller = WithTempData(new LogsController(_factory, _manager, MsOptions.Create(new DevDeckOptions())));

        var result = await controller.Download(runId);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Details");
        controller.TempData["Error"].Should().BeOfType<string>().Which.Should().Contain("no longer exists");
    }

    private ServicesController ServicesController()
    {
        var options = MsOptions.Create(new DevDeckOptions());
        _proxyProvider = new DevDeckProxyConfigProvider(_factory, Builder(), NullLogger<DevDeckProxyConfigProvider>.Instance);
        return WithTempData(new ServicesController(
            _factory,
            _manager,
            new CommandPresetProvider(new CommandExecutableResolver()),
            new PortProbeService(_manager),
            new PortabilityExporter(_factory),
            new PortabilityImporter(_factory, _manager),
            _proxyProvider,
            options,
            _healthCache));
    }

    private ProxyRoutesController RoutesController()
    {
        var builder = Builder();
        _proxyProvider = new DevDeckProxyConfigProvider(_factory, builder, NullLogger<DevDeckProxyConfigProvider>.Instance);
        return WithTempData(new ProxyRoutesController(
            _factory,
            _proxyProvider,
            new ProxyDestinationValidator(allowExternal: false),
            builder,
            new CommandTemplateRenderer(),
            new PortProbeService(_manager),
            _manager,
            MsOptions.Create(new DevDeckOptions()),
            new PortabilityExporter(_factory),
            new PortabilityImporter(_factory),
            new HealthStatusCache()));
    }

    private ProfilesController ProfilesController() =>
        WithTempData(new ProfilesController(_factory, _manager, new PortabilityExporter(_factory), new PortabilityImporter(_factory)));

    private static T WithTempData<T>(T controller) where T : Controller
    {
        controller.TempData = new TempDataDictionary(new DefaultHttpContext(), new NullTempDataProvider());
        return controller;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
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

    private async Task<int> SeedServiceAsync(string? url = null)
    {
        await using var db = _factory.CreateDbContext();
        var service = new DevService
        {
            Name = "api",
            ServiceType = "NodeApi",
            WorkingDirectory = AppContext.BaseDirectory,
            StartCommand = "npm",
            Port = 3001,
            Url = url,
        };
        db.DevServices.Add(service);
        await db.SaveChangesAsync();
        return service.Id;
    }

    private async Task<int> SeedHealthCheckAsync(int serviceId, string url)
    {
        await using var db = _factory.CreateDbContext();
        var check = new ServiceHealthCheck { DevServiceId = serviceId, Url = url };
        db.ServiceHealthChecks.Add(check);
        await db.SaveChangesAsync();
        return check.Id;
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

        public HashSet<int> Busy { get; } = new();

        public bool IsServiceBusy(int serviceId) => Busy.Contains(serviceId);

        public HashSet<int> Starting { get; } = new();

        public bool IsServiceStarting(int serviceId) => Starting.Contains(serviceId);

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
