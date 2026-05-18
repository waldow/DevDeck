using DevDeck.Web.Data;
using DevDeck.Web.Options;
using DevDeck.Web.Services.Commands;
using DevDeck.Web.Services.Health;
using DevDeck.Web.Services.Logs;
using DevDeck.Web.Services.Proxy;
using DevDeck.Web.Services.Runtime;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Kestrel binding — DevDeck gateway listens on 5050 by default (spec §33).
builder.WebHost.UseUrls("http://localhost:5050");

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
builder.Services.AddSingleton<DevDeckProcessManager>();
builder.Services.AddSingleton<IDevDeckProcessManager>(sp => sp.GetRequiredService<DevDeckProcessManager>());
builder.Services.AddSingleton<PortProbeService>();
builder.Services.AddSingleton<ProxyDestinationValidator>();
builder.Services.AddSingleton<ProxyRouteBuilder>();
builder.Services.AddSingleton<ProxyRequestGuard>();
builder.Services.AddSingleton<DevDeckProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<DevDeckProxyConfigProvider>());
builder.Services.AddHostedService<HealthCheckBackgroundService>();
builder.Services.AddHostedService<AutoStartHostedService>();

builder.Services.AddReverseProxy();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevDeckDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var proxyProvider = scope.ServiceProvider.GetRequiredService<DevDeckProxyConfigProvider>();
    await proxyProvider.ReloadAsync();
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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Important: reverse proxy routes are mapped AFTER MVC routes so /Manage always wins.
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
    });
}

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
                .Where(s => s.Enabled && s.AutoStart)
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
