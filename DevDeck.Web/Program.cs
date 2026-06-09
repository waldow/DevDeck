using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Portability;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Kestrel binding — DevDeck gateway listens on the configured gateway URL.
var listenUrl = GatewayUrlResolver.ResolveListenUrl(builder.Configuration);
builder.WebHost.UseUrls(listenUrl);

builder.Services.Configure<DevDeckOptions>(builder.Configuration.GetSection(DevDeckOptions.SectionName));

builder.Services.AddControllersWithViews();
builder.Services.AddAuthorization();
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

internal sealed class AutoStartHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly Microsoft.Extensions.Options.IOptions<DevDeckOptions> _options;
    private readonly ILogger<AutoStartHostedService> _logger;

    public AutoStartHostedService(
        IServiceProvider services,
        Microsoft.Extensions.Options.IOptions<DevDeckOptions> options,
        ILogger<AutoStartHostedService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.AutoStartEnabledServices) return;

        try
        {
            using var scope = _services.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevDeckDbContext>>();
            var manager = scope.ServiceProvider.GetRequiredService<IDevDeckProcessManager>();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var ids = await db.DevServices
                .Where(s => s.Enabled && s.AutoStart && !s.UseExternalInstance)
                .OrderBy(s => s.DisplayOrder)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in ids)
            {
                await manager.StartServiceAsync(id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-start of enabled services failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Opt-in (DevDeck:StopServicesOnShutdown): stop all managed services when DevDeck shuts
// down. Default keeps today's behavior — children keep running and are reconciled as
// orphaned runs on the next start.
internal sealed class StopServicesOnShutdownHostedService : IHostedService
{
    private readonly IDevDeckProcessManager _manager;
    private readonly Microsoft.Extensions.Options.IOptions<DevDeckOptions> _options;
    private readonly ILogger<StopServicesOnShutdownHostedService> _logger;

    public StopServicesOnShutdownHostedService(
        IDevDeckProcessManager manager,
        Microsoft.Extensions.Options.IOptions<DevDeckOptions> options,
        ILogger<StopServicesOnShutdownHostedService> logger)
    {
        _manager = manager;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.StopServicesOnShutdown) return;

        try
        {
            var result = await _manager.StopAllAsync(cancellationToken);
            _logger.LogInformation("Stopped {Count} managed services on shutdown", result.Stopped);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop managed services on shutdown");
        }
    }
}
