using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Portability;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Configuration;

// Helper mode (Windows): raise Ctrl+C on a service's console so it can shut down gracefully;
// see WindowsConsoleSignal. Runs before anything else and exits.
if (WindowsConsoleSignal.IsHelperInvocation(args))
{
    return WindowsConsoleSignal.RunHelper(args);
}

var builder = WebApplication.CreateBuilder(args);

// Kestrel binding — DevDeck gateway listens on the configured gateway URL.
var listenUrl = GatewayUrlResolver.ResolveListenUrl(builder.Configuration);
builder.WebHost.UseUrls(listenUrl);

builder.Services.Configure<DevDeckOptions>(builder.Configuration.GetSection(DevDeckOptions.SectionName));

// StopServicesOnShutdown stops services concurrently, each taking up to StopTimeoutSeconds
// for its stop command and about as long again for the process to exit, plus the 5s kill
// fallback and exit finalization; give the host long enough for that.
var shutdownOptions = builder.Configuration.GetSection(DevDeckOptions.SectionName).Get<DevDeckOptions>() ?? new DevDeckOptions();
if (shutdownOptions.StopServicesOnShutdown)
{
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(
        Math.Max(o.ShutdownTimeout.TotalSeconds, 2 * Math.Max(1, shutdownOptions.StopTimeoutSeconds) + 15)));
}

// Blank rows in the service editor (an env var or health check added and left empty, or
// removed with ✕) bind as null; the controller skips them, so don't let nullable reference
// types turn every non-nullable string on a row into an implicit [Required].
builder.Services.AddControllersWithViews(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
builder.Services.AddAuthorization();

// DevDeck has no authentication, so it must only answer requests addressed to its own host
// names (AllowedHosts) — otherwise a DNS-rebinding page could drive the Manage UI from the
// developer's browser. The loopback names and a concrete gateway host are always allowed.
builder.Services.PostConfigure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
    o => AllowedHostsPolicy.IncludeDevDeckHosts(o, listenUrl));
builder.Services.AddSingleton<AllowedHostsPolicy>();
builder.Services.AddHttpClient();

builder.Services.AddDbContextFactory<DevDeckDbContext>(options =>
    options.UseSqlite(DevDeckPaths.SqliteConnectionString));

builder.Services.AddSingleton<CommandExecutableResolver>();
builder.Services.AddSingleton<CommandTemplateRenderer>();
builder.Services.AddSingleton<CommandPresetProvider>();
builder.Services.AddSingleton<LogFileWriter>();
builder.Services.AddSingleton<ProcessLogBuffer>();
builder.Services.AddSingleton<HealthStatusCache>();
builder.Services.AddSingleton<IAzuriteSupervisor, AzuriteSupervisor>();
builder.Services.AddSingleton<DevDeckProcessManager>();
builder.Services.AddSingleton<IDevDeckProcessManager>(sp => sp.GetRequiredService<DevDeckProcessManager>());
builder.Services.AddSingleton<RunHistoryRefreshService>();
builder.Services.AddSingleton<PortProbeService>();
builder.Services.AddSingleton<ProxyDestinationValidator>();
builder.Services.AddSingleton<ProxyRouteBuilder>();
builder.Services.AddSingleton<ProxyRequestGuard>();
// Keeps reserved paths (/Manage, static assets) from ever being matched by a proxy route.
builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, ReservedPathMatcherPolicy>());
builder.Services.AddSingleton<ProxyRequestLogger>();
builder.Services.AddSingleton<DevDeckProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DevDeckProxyConfigProvider>());
builder.Services.AddSingleton<PortabilityExporter>();
builder.Services.AddSingleton<PortabilityImporter>();
builder.Services.AddHostedService<HealthCheckBackgroundService>();
builder.Services.AddHostedService<AutoStartHostedService>();
builder.Services.AddHostedService<StopServicesOnShutdownHostedService>();
builder.Services.AddHostedService<LogRetentionService>();

builder.Services.AddReverseProxy();

var app = builder.Build();

if (!GatewayUrlResolver.IsLoopbackHost(listenUrl))
{
    app.Logger.LogWarning(
        "DevDeck is binding to {ListenUrl}, which is reachable from other machines. " +
        "DevDeck has no authentication — prefer a localhost gateway URL.",
        listenUrl);
}

var allowedHosts = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>>().Value.AllowedHosts;
if (allowedHosts.Contains("*"))
{
    app.Logger.LogWarning(
        "AllowedHosts is '*', so DevDeck answers requests for any host name. DevDeck has no authentication: " +
        "a web page whose host name re-resolves to this machine (DNS rebinding) could then drive it. " +
        "List the host names you use instead, e.g. \"localhost;*.localhost;127.0.0.1;[::1]\".");
}

try
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevDeckDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var proxyProvider = scope.ServiceProvider.GetRequiredService<DevDeckProxyConfigProvider>();
    await proxyProvider.ReloadAsync();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex,
        "DevDeck failed to initialize its database at {DatabaseFile}. " +
        "Fix the file's permissions or delete it (configuration will be lost) and restart.",
        DevDeckPaths.DatabaseFile);
    throw;
}

// Services an earlier DevDeck session left running (it was killed or crashed, or
// StopServicesOnShutdown is off) are re-attached, so they show as running, are proxied to and
// can be stopped, instead of being launched a second time; runs whose process has died since
// are closed out.
try
{
    var adopted = await app.Services.GetRequiredService<DevDeckProcessManager>().AdoptOrphanedRunsAsync();
    await app.Services.GetRequiredService<RunHistoryRefreshService>().RefreshActiveRunsAsync();
    if (adopted > 0)
    {
        app.Logger.LogInformation("Re-attached {Count} service(s) left running by an earlier DevDeck session", adopted);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Could not reconcile services left running by an earlier DevDeck session");
}

app.UseStaticFiles();
app.UseRouting();

var devDeckOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DevDeckOptions>>().Value;
if (devDeckOptions.DevelopmentOnly && !app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.Equals("/Manage", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Manage/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("DevDeck management is only available in Development.");
            return;
        }

        await next();
    });
}

app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

// Important: management routes are mapped before the reverse proxy so /Manage always wins.
if (devDeckOptions.ReverseProxy.Enabled)
{
    app.MapReverseProxy(proxyPipeline =>
    {
        proxyPipeline.Use(async (context, next) =>
        {
            var guard = context.RequestServices.GetRequiredService<ProxyRequestGuard>();
            if (await guard.AllowRequestAsync(context))
            {
                await next();
            }
        });
        proxyPipeline.Use(async (context, next) =>
        {
            var logger = context.RequestServices.GetRequiredService<ProxyRequestLogger>();
            await logger.LogExchangeAsync(context, next);
        });
    });
}

// Root redirects to Manage only when no proxy route, such as /{**catch-all}, handles it first.
app.MapGet("/", () => Results.Redirect("/Manage")).WithOrder(int.MaxValue);

app.Run();
return 0;

// Starts the enabled AutoStart services once DevDeck is listening. Not from StartAsync: the web
// server only starts after every hosted service's StartAsync has returned, so a slow start
// (Azurite coming up for a Functions host) would keep the dashboard and gateway offline, and a
// second DevDeck instance would launch duplicates before failing to bind its port.
internal sealed class AutoStartHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Microsoft.Extensions.Options.IOptions<DevDeckOptions> _options;
    private readonly ILogger<AutoStartHostedService> _logger;

    public AutoStartHostedService(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        Microsoft.Extensions.Options.IOptions<DevDeckOptions> options,
        ILogger<AutoStartHostedService> logger)
    {
        _services = services;
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.AutoStartEnabledServices) return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (_lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled()))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        List<(int Id, string Name)> services;
        try
        {
            var dbFactory = _services.GetRequiredService<IDbContextFactory<DevDeckDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            services = await db.DevServices
                .Where(s => s.Enabled && s.AutoStart && !s.UseExternalInstance)
                .OrderBy(s => s.DisplayOrder)
                .Select(s => new ValueTuple<int, string>(s.Id, s.Name))
                .ToListAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Auto-start of enabled services failed");
            return;
        }

        var manager = _services.GetRequiredService<IDevDeckProcessManager>();
        foreach (var (id, name) in services)
        {
            if (stoppingToken.IsCancellationRequested || _lifetime.ApplicationStopping.IsCancellationRequested) return;
            if (manager.GetRunningProcess(id) is not null) continue; // e.g. re-attached from an earlier session

            // One failure must not cost the rest their start.
            try
            {
                var result = await manager.StartServiceAsync(id, stoppingToken);
                if (!result.Success)
                {
                    _logger.LogWarning("Auto-start of {ServiceName} failed: {Error}", name, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-start of {ServiceName} failed", name);
            }
        }
    }
}

// DevDeck:StopServicesOnShutdown (on by default): stop all managed services when DevDeck shuts
// down. When it is off they are left running and re-attached on the next start.
// The stop runs in StoppingAsync, before the web server shuts down: the server first waits for
// open connections (a proxied HMR websocket holds one until the host's shutdown timeout), and
// that wait would otherwise use up the shared shutdown token before the services were reached.
// Stopping the services first also ends those proxied connections.
internal sealed class StopServicesOnShutdownHostedService : IHostedLifecycleService
{
    private readonly DevDeckProcessManager _manager;
    private readonly Microsoft.Extensions.Options.IOptions<DevDeckOptions> _options;
    private readonly ILogger<StopServicesOnShutdownHostedService> _logger;

    public StopServicesOnShutdownHostedService(
        DevDeckProcessManager manager,
        Microsoft.Extensions.Options.IOptions<DevDeckOptions> options,
        ILogger<StopServicesOnShutdownHostedService> logger)
    {
        _manager = manager;
        _options = options;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Whether or not services are stopped, nothing new is launched while DevDeck goes down
        // (an auto-start or request in flight would otherwise start one after the Stop-all).
        _manager.BeginShutdown();
        if (!_options.Value.StopServicesOnShutdown) return;

        try
        {
            // The stops themselves are bounded by StopTimeoutSeconds; the host's token only
            // limits how long shutdown waits for them.
            var result = await _manager.StopAllAsync(CancellationToken.None).WaitAsync(cancellationToken);
            _logger.LogInformation("Stopped {Count} managed services on shutdown", result.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop managed services on shutdown");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
