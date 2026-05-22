# DevDeck Specification

**Project name:** DevDeck  
**Project type:** ASP.NET Core MVC local development orchestration dashboard  
**Target stack:** C# / .NET 10 MVC, EF Core, SQLite, YARP reverse proxy  
**Primary purpose:** Manage local development services from one web UI: Azure Functions, React/Vite frontends, Node apps, .NET APIs, Docker Compose services, custom commands, and optional local reverse-proxy routes.

---

## 1. Product Summary

DevDeck is a local developer dashboard that can start, stop, restart, monitor, and view logs for multiple local development processes.

Examples of processes DevDeck should manage:

```text
Azure Functions  -> func start --port 7071
React / Vite     -> npm run dev -- --host 0.0.0.0 --port 5173
React CRA        -> npm start
Node APIs        -> npm run dev
.NET APIs        -> dotnet run --urls http://localhost:5080
Docker Compose   -> docker compose up
Custom tools     -> any configured executable + arguments
```

Examples of local reverse-proxy routes DevDeck should support:

```text
http://localhost:5050/app/...       -> http://localhost:5173/...
http://localhost:5050/api/...       -> http://localhost:5080/api/...
http://localhost:5050/functions/... -> http://localhost:7071/api/...
```

The reverse proxy should let the developer use one local gateway URL for multiple services. This is useful for reducing CORS friction, using friendly URLs, and testing frontend/API integration from one origin.

The application should include a `/Manage` sub-site where the user can configure services, launch profiles, view process status, view logs, configure reverse-proxy routes, and perform common actions.

DevDeck is intended primarily as a **local development tool**, not a production process manager or production ingress gateway.

---

## 2. Core Goals

1. Provide a clean management UI for local dev services.
2. Store service definitions and run history in SQLite.
3. Spawn configured processes safely using `System.Diagnostics.Process`.
4. Stream stdout and stderr into live logs.
5. Store persistent logs as files, not as large SQLite blobs.
6. Support start, stop, restart, and open-in-browser actions.
7. Support launch profiles such as “Full Stack”, “Frontend Only”, or “Functions Only”.
8. Support health checks for services with URLs.
9. Support reverse-proxy routes so services can be reached through one DevDeck gateway.
10. Support presets for common service types.
11. Be easy for a coding agent to build incrementally.

---

## 3. Non-Goals

DevDeck v1 does **not** need to:

1. Run as a cloud-hosted production app.
2. Replace Docker Compose, PM2, Azure Functions Core Tools, or Visual Studio launch profiles.
3. Provide multi-user collaboration.
4. Store every log line in SQLite.
5. Support advanced terminal emulation.
6. Implement full shell scripting.
7. Automatically discover every project in a repository.
8. Replace Nginx, Traefik, Azure Application Gateway, or production ingress tooling.
9. Provide production-grade TLS/certificate automation in v1.

---

## 4. Safety Constraints

Because DevDeck spawns local processes, safety matters.

### 4.1 Development-only by default

DevDeck should default to development/local usage.

Recommended guard:

```csharp
if (!app.Environment.IsDevelopment())
{
    // Either disable /Manage execution actions or require explicit opt-in config.
}
```

### 4.2 No raw public command execution

Do not expose an API endpoint where arbitrary anonymous users can send command strings to execute.

Bad:

```http
POST /api/run
{
  "command": "anything from the internet"
}
```

Good:

```http
POST /Manage/Services/12/Start
```

The command should come from a stored, trusted service definition.

### 4.3 Validate paths

When creating or editing a service:

- Ensure `WorkingDirectory` exists.
- Ensure `StartCommand` is present.
- Optionally verify the command exists on PATH or exists as an absolute file path.
- Warn if the configured port is already used by another service.

### 4.4 Do not store real secrets casually

Environment variables may contain secrets. For v1, allow the user to mark environment variables as secret and mask them in the UI.

For v1, storing secrets in SQLite is acceptable only for a local development tool, but the UI must clearly mask them. Future versions can encrypt them using DPAPI or platform-specific secret storage.

### 4.5 Reverse proxy safety

The reverse proxy must not make `/Manage` accidentally reachable through proxy routes or allow proxy routes to silently hijack DevDeck's own management endpoints.

Rules:

- `/Manage` is reserved for DevDeck UI and must never be proxied.
- `/manage` should also be treated as reserved because URL matching can be case-insensitive in some environments.
- Static assets used by DevDeck itself should be reserved, such as `/css`, `/js`, `/lib`, `/images`, and `/_devdeck`.
- A catch-all proxy route such as `/{**catch-all}` should be disabled by default and require an explicit warning/confirmation in the UI.
- In v1, proxy destinations should default to localhost or private-network targets only.
- External proxy destinations may be supported later, but should require an explicit `AllowExternalDestinations` setting.
- Proxy routes should be editable only through the authenticated/local `/Manage` site.

---

## 5. High-Level Architecture

```text
DevDeck.Web
├── MVC Manage Area
│   ├── Dashboard
│   ├── Services
│   ├── Profiles
│   ├── Runs
│   ├── Logs
│   └── Settings
│
├── SQLite Database
│   ├── Service definitions
│   ├── Environment variables
│   ├── Health check definitions
│   ├── Reverse proxy route definitions
│   ├── Launch profiles
│   └── Run history summaries
│
├── Runtime Services
│   ├── DevDeckProcessManager
│   ├── ProcessLogBuffer
│   ├── LogFileWriter
│   ├── HealthCheckService
│   ├── PortProbeService
│   └── DevDeckProxyConfigProvider
│
├── Reverse Proxy Gateway
│   ├── YARP routes
│   ├── path-prefix transforms
│   ├── service destination mapping
│   └── optional auto-start-on-request behavior
│
└── Local Processes
    ├── func start
    ├── npm run dev
    ├── npm start
    ├── dotnet run
    ├── docker compose up
    └── custom commands
```

Important separation:

```text
SQLite = persistent configuration and history
Memory = live process objects, streams, cancellation tokens, status
Log files = durable stdout/stderr output
```

---

## 6. Recommended Project Structure

```text
DevDeck.Web/
├── Areas/
│   └── Manage/
│       ├── Controllers/
│       │   ├── DashboardController.cs
│       │   ├── ServicesController.cs
│       │   ├── ProfilesController.cs
│       │   ├── RunsController.cs
│       │   ├── LogsController.cs
│       │   ├── ProxyRoutesController.cs
│       │   └── SettingsController.cs
│       │
│       ├── ViewModels/
│       │   ├── DashboardViewModel.cs
│       │   ├── ServiceEditViewModel.cs
│       │   ├── ServiceListItemViewModel.cs
│       │   ├── ProfileEditViewModel.cs
│       │   ├── ProxyRouteEditViewModel.cs
│       │   └── RunDetailsViewModel.cs
│       │
│       └── Views/
│           ├── Dashboard/
│           │   └── Index.cshtml
│           ├── Services/
│           │   ├── Index.cshtml
│           │   ├── Create.cshtml
│           │   ├── Edit.cshtml
│           │   ├── Details.cshtml
│           │   └── Logs.cshtml
│           ├── Profiles/
│           │   ├── Index.cshtml
│           │   ├── Create.cshtml
│           │   └── Edit.cshtml
│           ├── Runs/
│           │   ├── Index.cshtml
│           │   └── Details.cshtml
│           ├── ProxyRoutes/
│           │   ├── Index.cshtml
│           │   ├── Create.cshtml
│           │   └── Edit.cshtml
│           ├── Settings/
│           │   └── Index.cshtml
│           └── Shared/
│               └── _ManageLayout.cshtml
│
├── Data/
│   ├── DevDeckDbContext.cs
│   ├── Entities/
│   │   ├── DevService.cs
│   │   ├── ServiceEnvironmentVariable.cs
│   │   ├── ServiceHealthCheck.cs
│   │   ├── ServiceRun.cs
│   │   ├── LaunchProfile.cs
│   │   ├── LaunchProfileService.cs
│   │   ├── ProxyRoute.cs
│   │   └── AppSetting.cs
│   └── Seed/
│       └── DevDeckSeeder.cs
│
├── Services/
│   ├── Runtime/
│   │   ├── DevDeckProcessManager.cs
│   │   ├── RunningProcessInfo.cs
│   │   ├── ProcessStartRequest.cs
│   │   ├── ProcessStatus.cs
│   │   └── ProcessLogBuffer.cs
│   │
│   ├── Logs/
│   │   ├── LogFileWriter.cs
│   │   └── LogLine.cs
│   │
│   ├── Health/
│   │   ├── HealthCheckBackgroundService.cs
│   │   ├── ServiceHealthStatus.cs
│   │   └── PortProbeService.cs
│   │
│   ├── Proxy/
│   │   ├── DevDeckProxyConfigProvider.cs
│   │   ├── DevDeckProxyConfigSnapshot.cs
│   │   ├── ProxyRouteBuilder.cs
│   │   ├── ProxyRouteReloadService.cs
│   │   └── ProxyDestinationValidator.cs
│   │
│   └── Commands/
│       ├── CommandPresetProvider.cs
│       ├── CommandTemplateRenderer.cs
│       └── CommandExecutableResolver.cs
│
├── wwwroot/
│   ├── css/
│   │   └── manage.css
│   └── js/
│       ├── dashboard.js
│       └── logs.js
│
├── appsettings.json
├── appsettings.Development.json
└── Program.cs
```

---

## 7. Data Storage

### 7.1 SQLite database location

Use a local application data folder.

Recommended Windows path:

```text
%LOCALAPPDATA%\DevDeck\devdeck.db
```

Cross-platform C# setup:

```csharp
var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var devDeckFolder = Path.Combine(appData, "DevDeck");
Directory.CreateDirectory(devDeckFolder);

var dbPath = Path.Combine(devDeckFolder, "devdeck.db");
```

### 7.2 Log file location

```text
%LOCALAPPDATA%\DevDeck\logs\
```

Recommended filename format:

```text
{service-slug}-{run-id}-{yyyyMMdd-HHmmss}.log
```

Example:

```text
baas-function-42-20260518-083000.log
frontend-43-20260518-083005.log
local-api-44-20260518-083010.log
```

---

## 8. Entity Model

### 8.1 DevService

Represents a configured local service.

```csharp
public sealed class DevService
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string ServiceType { get; set; }
    // AzureFunction, ReactVite, ReactCra, NodeApi, DotNetApi, DockerCompose, Custom

    public required string WorkingDirectory { get; set; }

    public required string StartCommand { get; set; }

    public string? StartArguments { get; set; }

    public string? StopCommand { get; set; }

    public string? StopArguments { get; set; }

    public string? Url { get; set; }

    public int? Port { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AutoStart { get; set; } = false;

    public int DisplayOrder { get; set; } = 0;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedUtc { get; set; }

    public List<ServiceEnvironmentVariable> EnvironmentVariables { get; set; } = [];

    public List<ServiceHealthCheck> HealthChecks { get; set; } = [];

    public List<ServiceRun> Runs { get; set; } = [];

    public List<ProxyRoute> ProxyRoutes { get; set; } = [];
}
```

### 8.2 ServiceEnvironmentVariable

```csharp
public sealed class ServiceEnvironmentVariable
{
    public int Id { get; set; }

    public int DevServiceId { get; set; }

    public DevService DevService { get; set; } = null!;

    public required string Key { get; set; }

    public required string Value { get; set; }

    public bool IsSecret { get; set; }
}
```

### 8.3 ServiceHealthCheck

```csharp
public sealed class ServiceHealthCheck
{
    public int Id { get; set; }

    public int DevServiceId { get; set; }

    public DevService DevService { get; set; } = null!;

    public required string Url { get; set; }

    public int ExpectedStatusCode { get; set; } = 200;

    public int IntervalSeconds { get; set; } = 10;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public string? LastStatus { get; set; }
    // Unknown, Healthy, Unhealthy, Timeout

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }
}
```

### 8.4 ServiceRun

Represents one process execution.

```csharp
public sealed class ServiceRun
{
    public long Id { get; set; }

    public int DevServiceId { get; set; }

    public DevService DevService { get; set; } = null!;

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset? StoppedUtc { get; set; }

    public int? ProcessId { get; set; }

    public int? ExitCode { get; set; }

    public required string Status { get; set; }
    // Starting, Running, Stopped, Crashed, Killed, FailedToStart

    public string? StartCommandSnapshot { get; set; }

    public string? StartArgumentsSnapshot { get; set; }

    public string? WorkingDirectorySnapshot { get; set; }

    public string? LogFilePath { get; set; }

    public string? LastError { get; set; }
}
```

### 8.5 LaunchProfile

Represents a named group of services that can be started together.

```csharp
public sealed class LaunchProfile
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool IsDefault { get; set; }

    public int DisplayOrder { get; set; }

    public List<LaunchProfileService> Services { get; set; } = [];
}
```

### 8.6 LaunchProfileService

Join table between profiles and services.

```csharp
public sealed class LaunchProfileService
{
    public int LaunchProfileId { get; set; }

    public LaunchProfile LaunchProfile { get; set; } = null!;

    public int DevServiceId { get; set; }

    public DevService DevService { get; set; } = null!;

    public int StartOrder { get; set; } = 0;

    public int StartDelaySeconds { get; set; } = 0;
}
```

### 8.7 ProxyRoute

Represents a reverse-proxy route that maps an incoming DevDeck URL to a configured service or explicit destination URL.

```csharp
public sealed class ProxyRoute
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public int? DevServiceId { get; set; }

    public DevService? DevService { get; set; }

    public string? DestinationUrlOverride { get; set; }
    // If null, use DevService.Url.

    public required string MatchPath { get; set; }
    // Example: /app/{**catch-all}, /api/{**catch-all}, /functions/{**catch-all}

    public string? MatchHostsCsv { get; set; }
    // Optional. Example: app.localhost,api.localhost

    public int Order { get; set; } = 0;

    public required string PathTransformMode { get; set; } = "None";
    // None, RemovePrefix, AddPrefix, RemoveAndAddPrefix, SetPath

    public string? PathPrefixToRemove { get; set; }
    // Example: /app

    public string? PathPrefixToAdd { get; set; }
    // Example: /api

    public string? PathSet { get; set; }

    public bool PreserveHostHeader { get; set; } = false;

    public bool AutoStartService { get; set; } = false;

    public bool RequireHealthyDestination { get; set; } = false;

    public int? TimeoutSeconds { get; set; }

    public string? AuthorizationPolicy { get; set; }
    // Anonymous, Default, or a named ASP.NET Core authorization policy.

    public bool ShowOnDashboard { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedUtc { get; set; }
}
```

### 8.8 AppSetting

```csharp
public sealed class AppSetting
{
    public int Id { get; set; }

    public required string Key { get; set; }

    public required string Value { get; set; }
}
```

---

## 9. EF Core DbContext

```csharp
using Microsoft.EntityFrameworkCore;

public sealed class DevDeckDbContext : DbContext
{
    public DevDeckDbContext(DbContextOptions<DevDeckDbContext> options)
        : base(options)
    {
    }

    public DbSet<DevService> DevServices => Set<DevService>();

    public DbSet<ServiceEnvironmentVariable> ServiceEnvironmentVariables =>
        Set<ServiceEnvironmentVariable>();

    public DbSet<ServiceHealthCheck> ServiceHealthChecks =>
        Set<ServiceHealthCheck>();

    public DbSet<ServiceRun> ServiceRuns => Set<ServiceRun>();

    public DbSet<LaunchProfile> LaunchProfiles => Set<LaunchProfile>();

    public DbSet<LaunchProfileService> LaunchProfileServices =>
        Set<LaunchProfileService>();

    public DbSet<ProxyRoute> ProxyRoutes => Set<ProxyRoute>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevService>()
            .HasIndex(x => x.Name)
            .IsUnique();

        modelBuilder.Entity<DevService>()
            .Property(x => x.ServiceType)
            .HasMaxLength(64);

        modelBuilder.Entity<ServiceEnvironmentVariable>()
            .HasIndex(x => new { x.DevServiceId, x.Key })
            .IsUnique();

        modelBuilder.Entity<ServiceRun>()
            .HasIndex(x => new { x.DevServiceId, x.StartedUtc });

        modelBuilder.Entity<LaunchProfileService>()
            .HasKey(x => new { x.LaunchProfileId, x.DevServiceId });

        modelBuilder.Entity<ProxyRoute>()
            .HasIndex(x => x.Name)
            .IsUnique();

        modelBuilder.Entity<ProxyRoute>()
            .HasIndex(x => x.MatchPath);

        modelBuilder.Entity<ProxyRoute>()
            .HasOne(x => x.DevService)
            .WithMany(x => x.ProxyRoutes)
            .HasForeignKey(x => x.DevServiceId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AppSetting>()
            .HasIndex(x => x.Key)
            .IsUnique();
    }
}
```

---

## 10. Program.cs Setup

```csharp
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var devDeckFolder = Path.Combine(appData, "DevDeck");
Directory.CreateDirectory(devDeckFolder);

var dbPath = Path.Combine(devDeckFolder, "devdeck.db");

builder.Services.AddDbContextFactory<DevDeckDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddSingleton<DevDeckProcessManager>();
builder.Services.AddSingleton<ProcessLogBuffer>();
builder.Services.AddSingleton<LogFileWriter>();
builder.Services.AddSingleton<PortProbeService>();
builder.Services.AddSingleton<CommandPresetProvider>();
builder.Services.AddSingleton<CommandTemplateRenderer>();
builder.Services.AddSingleton<DevDeckProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp =>
    sp.GetRequiredService<DevDeckProxyConfigProvider>());
builder.Services.AddSingleton<ProxyRouteBuilder>();
builder.Services.AddSingleton<ProxyDestinationValidator>();
builder.Services.AddHostedService<HealthCheckBackgroundService>();

builder.Services.AddReverseProxy();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DevDeckDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();

    var seeder = new DevDeckSeeder(db);
    await seeder.SeedAsync();
}

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Map reverse proxy endpoints after MVC routes. Also validate proxy routes so they
// do not collide with /Manage or DevDeck static assets.
app.MapReverseProxy();

app.Run();
```

---

## 11. Process Management

### 11.1 Runtime status enum

```csharp
public enum ProcessStatus
{
    Unknown = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Crashed = 5,
    FailedToStart = 6,
    Killed = 7
}
```

### 11.2 RunningProcessInfo

```csharp
public sealed class RunningProcessInfo
{
    public required int DevServiceId { get; init; }

    public required long ServiceRunId { get; init; }

    public required string ServiceName { get; init; }

    public required Process Process { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required string LogFilePath { get; init; }

    public ProcessStatus Status { get; set; } = ProcessStatus.Starting;

    public int? Port { get; init; }

    public string? Url { get; init; }
}
```

### 11.3 DevDeckProcessManager responsibilities

The process manager should be a singleton and maintain a thread-safe in-memory dictionary:

```csharp
ConcurrentDictionary<int, RunningProcessInfo> _runningProcesses;
```

Responsibilities:

1. Start a configured service.
2. Stop a running service.
3. Restart a service.
4. Start all services in a launch profile.
5. Stop all running services.
6. Track process IDs and exit codes.
7. Capture stdout and stderr.
8. Write logs to memory buffer and log file.
9. Update `ServiceRun` rows when processes exit.
10. Prevent duplicate starts for the same service.

### 11.4 Start process behavior

When starting a service:

1. Load `DevService` from SQLite.
2. Validate `Enabled`.
3. Validate `WorkingDirectory` exists.
4. Check if service is already running.
5. Render command argument templates such as `{port}`.
6. Create a `ServiceRun` row with status `Starting`.
7. Create a log file path.
8. Configure `ProcessStartInfo`:

```csharp
var psi = new ProcessStartInfo
{
    FileName = resolvedCommand,
    Arguments = renderedArguments,
    WorkingDirectory = service.WorkingDirectory,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    RedirectStandardInput = true,
    CreateNoWindow = true,
};
```

9. Apply configured environment variables.
10. Start the process.
11. Begin async stdout/stderr reading.
12. Update `ServiceRun` to `Running` and store PID.
13. Add `RunningProcessInfo` to the in-memory dictionary.

### 11.5 Stop process behavior

For v1, stopping can use process tree kill:

```csharp
process.Kill(entireProcessTree: true);
```

Future enhancement: support graceful stop per service type:

- Send Ctrl+C for compatible console processes.
- Send `q` or newline to process stdin if configured.
- Run configured `StopCommand`.
- Then kill after timeout.

Recommended v1 behavior:

1. Mark status as `Stopping`.
2. Try graceful stop if configured.
3. Wait up to `StopTimeoutSeconds`.
4. Kill entire process tree if still running.
5. Update `ServiceRun` to `Killed` or `Stopped`.
6. Remove from runtime dictionary.

### 11.6 Restart process behavior

Restart is:

```text
Stop -> short delay -> Start
```

The restart endpoint should return the new run ID.

### 11.7 Azure Functions storage prerequisite (Azurite)

The local Functions host requires `AzureWebJobsStorage` (the Azure Storage emulator,
Azurite) and fails on startup when it is missing. To make `func start` reliable, the process
manager runs an **Azurite preflight before launching any service whose `ServiceType` is
`AzureFunction`** (this covers both the `func` and the `npx … func` command variants).

Preflight behavior:

1. **Probe** the emulator's TCP ports — blob `10000`, queue `10001`, table `10002`.
2. If all are listening, Azurite is considered healthy and **reused** as-is, regardless of
   whether DevDeck or the user started it.
3. If not, DevDeck launches the global **`azurite` CLI** (`azurite.cmd` on Windows, resolved
   against `PATH`) as a managed background process, then waits up to a timeout for the ports
   to come up before starting the Functions host.
4. If Azurite cannot be brought up (not installed, exited during startup, or timed out), the
   service start **fails fast** with status `FailedToStart` and a clear error rather than
   letting the Functions host crash cryptically.

Implementation notes:

- An `AzuriteSupervisor` singleton (`IAzuriteSupervisor`) owns the emulator process and
  serializes concurrent starts so multiple Functions services share one launch.
- Azurite's workspace lives under `<LocalAppData>/DevDeck/azurite/`; its stdout/stderr stream
  to `<LocalAppData>/DevDeck/logs/azurite.log`. Preflight progress is also written into the
  triggering service's run log as `SYS` lines.
- The DevDeck-launched emulator is stopped on host shutdown; a pre-existing/external Azurite
  is left running.
- Configurable via `DevDeck:Azurite` — `Command` (default `azurite`),
  `BlobPort`/`QueuePort`/`TablePort` (defaults `10000`/`10001`/`10002`), and
  `StartupTimeoutSeconds` (default `30`).

---

## 12. Command Presets

DevDeck should provide presets when creating a service.

### 12.1 Azure Functions

```text
ServiceType: AzureFunction
Command Windows: func.cmd
Command Linux/macOS: func
Arguments: start --port {port}
Default port: 7071
```

Alternative through npm/npx:

```text
Command Windows: npx.cmd
Command Linux/macOS: npx
Arguments: --yes --package azure-functions-core-tools@4 func start --port {port}
```

Starting any `AzureFunction` service triggers the Azurite storage preflight described in
§11.7 — Azurite is ensured healthy (or launched) before the Functions host starts.

### 12.2 React / Vite

```text
ServiceType: ReactVite
Command Windows: npm.cmd
Command Linux/macOS: npm
Arguments: run dev -- --host 0.0.0.0 --port {port}
Default port: 5173
```

### 12.3 React Create React App

```text
ServiceType: ReactCra
Command Windows: npm.cmd
Command Linux/macOS: npm
Arguments: start
Default port: 3000
```

Optional environment variable:

```text
PORT={port}
```

### 12.4 Node API

```text
ServiceType: NodeApi
Command Windows: npm.cmd
Command Linux/macOS: npm
Arguments: run dev
Default port: 3001
```

### 12.5 .NET API

```text
ServiceType: DotNetApi
Command Windows: dotnet.exe
Command Linux/macOS: dotnet
Arguments: run --urls http://localhost:{port}
Default port: 5080
```

### 12.6 Docker Compose

```text
ServiceType: DockerCompose
Command Windows: docker.exe
Command Linux/macOS: docker
Arguments: compose up
```

### 12.7 Custom

```text
ServiceType: Custom
Command: user-defined
Arguments: user-defined
```

---

## 13. Command Template Rendering

Support these placeholders in command arguments, URLs, and health check URLs:

```text
{id}
{name}
{port}
{workingDirectory}
```

Example:

```text
Arguments: run dev -- --port {port}
URL: http://localhost:{port}
Health check URL: http://localhost:{port}/health
```

The renderer should replace unknown placeholders with an empty string or leave them unchanged. Prefer leaving unknown placeholders unchanged and displaying a warning in the UI.

---

## 14. Executable Resolution

The command resolver should handle platform differences.

On Windows:

```text
npm     -> npm.cmd
npx     -> npx.cmd
func    -> func.cmd
node    -> node.exe
npm.cmd -> npm.cmd
```

On Linux/macOS:

```text
npm.cmd -> npm
npx.cmd -> npx
func.cmd -> func
node.exe -> node
```

If the command is an absolute path, use it as-is.

If the command is a simple executable name, let the OS resolve it from PATH.

---

## 15. Log Management

### 15.1 Live logs

Use an in-memory ring buffer per running service.

Recommended defaults:

```text
Max live log lines per service: 5,000
Trim amount: 1,000 lines
```

Each log line should include:

```csharp
public sealed class LogLine
{
    public DateTimeOffset Timestamp { get; init; }

    public required int DevServiceId { get; init; }

    public required long ServiceRunId { get; init; }

    public required string Stream { get; init; }
    // OUT, ERR, SYS

    public required string Text { get; init; }
}
```

### 15.2 Persistent logs

Write all stdout/stderr/system events to a `.log` file.

Example line format:

```text
2026-05-18T08:30:00.0000000+02:00 [OUT] VITE v5.0.0 ready in 420 ms
2026-05-18T08:30:01.0000000+02:00 [ERR] warning: something happened
2026-05-18T08:31:00.0000000+02:00 [SYS] Process exited with code 0
```

### 15.3 Do not store every log line in SQLite

SQLite should store run summaries only:

- started time
- stopped time
- exit code
- final status
- log file path
- last error

---

## 16. Health Checks

Health checks should run in a background hosted service.

Behavior:

1. Periodically load enabled health checks.
2. For each health check, make an HTTP GET request.
3. Compare returned status code to `ExpectedStatusCode`.
4. Update `LastCheckedUtc`, `LastStatus`, `LastStatusCode`, and `LastError`.
5. Dashboard should show health state.

Statuses:

```text
Unknown
Healthy
Unhealthy
Timeout
NotRunning
```

If a service is not running, display `NotRunning` rather than repeatedly calling its URL.

---

## 17. Port Checks

DevDeck should detect whether a configured port is already in use.

Use cases:

1. Warn when editing a service.
2. Warn before starting a service.
3. Show port status on dashboard.

Example statuses:

```text
Free
UsedByDevDeckService
UsedByOtherProcess
Unknown
```

For v1, it is enough to attempt a TCP connection to `localhost:{port}` and infer whether the port is open.

---

## 18. Reverse Proxy Gateway

Reverse proxying should be a first-class feature of DevDeck, not an afterthought.

The goal is to let the developer access multiple local services through one DevDeck gateway URL.

Example:

```text
DevDeck gateway: http://localhost:5050

/app/...       -> React/Vite frontend on http://localhost:5173/...
/api/...       -> .NET API on http://localhost:5080/api/...
/functions/... -> Azure Functions host on http://localhost:7071/api/...
```

This gives the local stack a single browser origin, which can reduce CORS problems and make frontend/API integration easier.

### 18.1 Recommended technology

Use **YARP** through the `Yarp.ReverseProxy` NuGet package.

YARP should be used because DevDeck is already an ASP.NET Core app, and YARP can run in-process with the MVC management site.

Required package:

```text
dotnet add package Yarp.ReverseProxy
```

Required high-level registration:

```csharp
builder.Services.AddSingleton<DevDeckProxyConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp =>
    sp.GetRequiredService<DevDeckProxyConfigProvider>());

builder.Services.AddReverseProxy();

// later, after MVC routes
app.MapReverseProxy();
```

The coding agent should implement a custom `IProxyConfigProvider` backed by SQLite and an in-memory snapshot. Do **not** write proxy configuration to `appsettings.json` every time the user edits a route.

### 18.2 Proxy route modes

Support these modes:

#### Mode 1: Direct open URL

This already exists conceptually.

```text
Open -> http://localhost:5173
```

No proxy involved.

#### Mode 2: Path-based reverse proxy

This is the v1 required proxy mode.

```text
http://localhost:5050/app/...       -> http://localhost:5173/...
http://localhost:5050/api/...       -> http://localhost:5080/api/...
http://localhost:5050/functions/... -> http://localhost:7071/api/...
```

#### Mode 3: Host-based reverse proxy

Future enhancement.

```text
http://app.localhost:5050/... -> http://localhost:5173/...
http://api.localhost:5050/... -> http://localhost:5080/...
```

Host-based routing is useful, but it may require browser/DNS/hosts-file considerations. Implement path-based routing first.

### 18.3 Route examples

#### React/Vite frontend

```text
Name: Frontend via DevDeck
Match path: /app/{**catch-all}
Destination: service URL for Frontend App, usually http://localhost:5173/
Transform mode: RemovePrefix
PathPrefixToRemove: /app
Result: /app/src/main.tsx -> http://localhost:5173/src/main.tsx
```

#### .NET API

Two valid patterns are possible.

Pattern A, destination already expects `/api`:

```text
Match path: /api/{**catch-all}
Destination: http://localhost:5080/
Transform mode: None
Result: /api/weather -> http://localhost:5080/api/weather
```

Pattern B, destination expects root paths:

```text
Match path: /api/{**catch-all}
Destination: http://localhost:5080/
Transform mode: RemovePrefix
PathPrefixToRemove: /api
Result: /api/weather -> http://localhost:5080/weather
```

Both should be supported through the UI.

#### Azure Functions

Most HTTP-triggered Azure Functions are exposed under `/api/...` by the local Functions host.

Useful route:

```text
Match path: /functions/{**catch-all}
Destination: http://localhost:7071/
Transform mode: RemoveAndAddPrefix
PathPrefixToRemove: /functions
PathPrefixToAdd: /api
Result: /functions/ping -> http://localhost:7071/api/ping
```

### 18.4 Default route helper

DevDeck can provide an automatic helper route for every service with a URL:

```text
/p/{service-slug}/{**catch-all} -> service.Url/{**catch-all}
```

Example:

```text
/p/frontend/... -> http://localhost:5173/...
/p/local-api/... -> http://localhost:5080/...
```

This can be implemented either as generated `ProxyRoute` rows or as a built-in dynamic route. Prefer generated rows because they are visible and editable in `/Manage/ProxyRoutes`.

### 18.5 Proxy route UI

Add a new management page:

```text
/Manage/ProxyRoutes
```

Navigation should include:

```text
Dashboard
Services
Proxy Routes
Profiles
Runs
Settings
```

Proxy route list columns:

```text
Name
Enabled
Match Path
Destination Service
Destination URL
Transform
Order
Actions
```

Actions:

```text
Open
Enable/Disable
Edit
Delete
Test
```

Create/edit fields:

```text
Name
Enabled
Destination Service
Destination URL Override
Match Path
Match Hosts
Order
Path Transform Mode
Path Prefix To Remove
Path Prefix To Add
Path Set
Preserve Host Header
Auto Start Service
Require Healthy Destination
Timeout Seconds
Authorization Policy
Show On Dashboard
```

### 18.6 Dashboard proxy links

Each service card should show both direct and proxy links when available.

Example:

```text
Frontend App
Direct: http://localhost:5173
Proxy:  http://localhost:5050/app

[Open Direct] [Open Proxy] [Logs] [Restart] [Stop]
```

The proxy link should be hidden if no enabled proxy route exists for that service.

### 18.7 Dynamic YARP configuration provider

Implement:

```csharp
public sealed class DevDeckProxyConfigProvider : IProxyConfigProvider
{
    public IProxyConfig GetConfig();

    public Task ReloadAsync(CancellationToken cancellationToken = default);
}
```

The provider should:

1. Load enabled `ProxyRoute` rows from SQLite.
2. Convert them into YARP `RouteConfig` and `ClusterConfig` objects.
3. Keep the latest full snapshot in memory.
4. Signal a change token when route definitions change.
5. Return the latest snapshot from `GetConfig()`.

Important behavior:

```text
SQLite -> ProxyRouteBuilder -> RouteConfig[] + ClusterConfig[] -> In-memory snapshot -> YARP reload
```

Do not query SQLite on every proxied request.

### 18.8 Proxy route builder

Implement a mapper service:

```csharp
public sealed class ProxyRouteBuilder
{
    public ProxyBuildResult Build(IEnumerable<ProxyRoute> routes);
}
```

Responsibilities:

1. Validate routes.
2. Resolve destination URL:
   - use `DestinationUrlOverride` if present
   - else use `DevService.Url`
3. Normalize destination URL to include a trailing slash.
4. Create one YARP cluster per proxy route for v1.
5. Apply path transforms.
6. Apply timeout configuration if provided.
7. Apply authorization policy if provided.
8. Exclude disabled or invalid routes and return warnings.

### 18.9 Path transforms

Support these transform modes:

```text
None
RemovePrefix
AddPrefix
RemoveAndAddPrefix
SetPath
```

Mapping:

```text
None                  -> no path transform
RemovePrefix          -> PathRemovePrefix
AddPrefix             -> PathPrefix
RemoveAndAddPrefix    -> PathRemovePrefix, then PathPrefix
SetPath               -> PathSet
```

Examples:

```text
Incoming: /app/dashboard
RemovePrefix /app
Outgoing: /dashboard
```

```text
Incoming: /functions/ping
RemovePrefix /functions
AddPrefix /api
Outgoing: /api/ping
```

### 18.10 Reserved paths

The proxy route validator must reject or warn for routes that collide with DevDeck itself.

Reserved prefixes:

```text
/Manage
/manage
/css
/js
/lib
/images
/favicon.ico
/_devdeck
```

The following should require explicit advanced override and should not be allowed by default:

```text
/{**catch-all}
/{*catch-all}
/
```

### 18.11 Proxy destination validation

For v1, allow by default:

```text
http://localhost:port
http://127.0.0.1:port
http://[::1]:port
http://*.localhost:port
```

Optionally allow private network ranges:

```text
10.0.0.0/8
172.16.0.0/12
192.168.0.0/16
```

Block or warn for public internet destinations unless this setting is explicitly enabled:

```json
{
  "DevDeck": {
    "ReverseProxy": {
      "AllowExternalDestinations": false
    }
  }
}
```

### 18.12 Auto-start on proxy request

This is useful but can be implemented after basic proxying works.

If `AutoStartService` is enabled and a proxied service is stopped:

1. Proxy middleware detects the matched service.
2. Start the service through `IDevDeckProcessManager`.
3. Wait for either:
   - port open
   - health check healthy
   - timeout
4. Continue proxying if ready.
5. Return `503 Service Unavailable` if the service fails to start.

For v1, acceptable behavior is simpler:

- If the service is stopped, return a friendly `503` page with a link to start it in `/Manage`.
- Implement actual auto-start as a later enhancement.

### 18.13 Proxy request status page

Add a route test action:

```http
GET /Manage/ProxyRoutes/{id}/Test
```

The test should show:

```text
Route enabled: yes/no
Match path: /app/{**catch-all}
Destination URL: http://localhost:5173/
Destination port open: yes/no
Service running: yes/no
Health status: Healthy/Unhealthy/Unknown
Example proxy URL: http://localhost:5050/app/
```

### 18.14 Proxy and logs

Do not log every proxied request to SQLite in v1.

For v1:

- Write proxy warnings/errors to application logs.
- Show route test results in the UI.
- Optionally keep a small in-memory counter per route:
  - total requests
  - last request UTC
  - last status code
  - last proxy error

Future enhancement: persistent request history.

### 18.15 React/Vite and HMR considerations

React/Vite development servers may use WebSockets for hot module replacement.

Requirements:

1. Proxy route should not block WebSocket upgrade requests.
2. Test Vite HMR through the proxy route.
3. If HMR connects directly to `localhost:5173`, that is acceptable for v1.
4. If HMR must go through DevDeck, document that Vite may need config such as `server.hmr.clientPort` or related host/client settings.
5. Do not make HMR perfection block the initial proxy milestone.

### 18.16 CORS behavior

One benefit of proxying is that the frontend and APIs can appear under one origin.

Example:

```text
Frontend: http://localhost:5050/app
API:      http://localhost:5050/api
```

The browser sees one origin:

```text
http://localhost:5050
```

This can reduce the need for local CORS setup. However, backend services may still need correct base path, forwarded header, and cookie settings.

### 18.17 Forwarded headers and host behavior

Default behavior should be:

```text
PreserveHostHeader: false
```

This lets the upstream service see its own host such as `localhost:5173` or `localhost:5080`.

Advanced option:

```text
PreserveHostHeader: true
```

This may be useful for apps that need the external DevDeck host. It can also confuse dev servers, so keep it off by default.

### 18.18 Proxy configuration example

Example persisted rows:

```text
Name: Frontend
Service: Frontend App
MatchPath: /app/{**catch-all}
DestinationUrlOverride: null
PathTransformMode: RemovePrefix
PathPrefixToRemove: /app
Order: 0
Enabled: true
```

```text
Name: Local API
Service: Local API
MatchPath: /api/{**catch-all}
DestinationUrlOverride: null
PathTransformMode: None
Order: 10
Enabled: true
```

```text
Name: Functions
Service: BAAS Function App
MatchPath: /functions/{**catch-all}
DestinationUrlOverride: null
PathTransformMode: RemoveAndAddPrefix
PathPrefixToRemove: /functions
PathPrefixToAdd: /api
Order: 20
Enabled: true
```

### 18.19 Proxy acceptance criteria

Reverse proxy work is accepted when:

1. User can create a proxy route linked to a service.
2. User can open a React/Vite app through DevDeck proxy.
3. User can call a .NET API through DevDeck proxy.
4. User can call an Azure Function through DevDeck proxy.
5. `/Manage` is never hijacked by proxy routing.
6. Prefix removal/addition works.
7. Proxy routes reload without restarting DevDeck.
8. Disabled proxy routes stop matching.
9. Dashboard shows proxy links.
10. Invalid routes show clear validation errors.


---

## 19. Manage Area UI

### 19.1 Base route

```text
/Manage
```

### 19.2 Navigation

The manage layout should include:

```text
Dashboard
Services
Proxy Routes
Profiles
Runs
Settings
```

Optional right-side links:

```text
Stop All
Start Default Profile
Open Logs Folder
```

---

## 20. Dashboard Page

Route:

```text
GET /Manage
GET /Manage/Dashboard
```

Purpose: Show all services and their current runtime status.

Each service card should show:

- Name
- Type
- Status
- Health state
- Port
- URL
- Last run status
- Start button
- Stop button
- Restart button
- Logs button
- Open URL button

Example card:

```text
BAAS Function App
Azure Function
Status: Running
Health: Healthy
Port: 7071
URL: http://localhost:7071

[Open] [Logs] [Restart] [Stop]
```

Dashboard should support basic polling every 1-2 seconds for status updates. SignalR is optional for v1.

---

## 21. Services Page

Route:

```text
GET /Manage/Services
```

Table columns:

```text
Name
Type
Working Directory
Port
Status
Health
Actions
```

Actions:

```text
Start
Stop
Restart
Logs
Edit
Delete
Open
```

### 21.1 Create Service

Route:

```text
GET /Manage/Services/Create
POST /Manage/Services/Create
```

Fields:

```text
Name
Service Type
Working Directory
Start Command
Start Arguments
Stop Command
Stop Arguments
Port
URL
Enabled
Auto Start
Environment Variables
Health Checks
```

The create screen should include preset buttons:

```text
Azure Function
React / Vite
React CRA
Node API
.NET API
Docker Compose
Custom
```

Selecting a preset fills command, arguments, default port, URL, and optional health check.

### 21.2 Edit Service

Route:

```text
GET /Manage/Services/Edit/{id}
POST /Manage/Services/Edit/{id}
```

Same fields as create.

If the service is currently running, editing process-related fields should display a warning:

```text
This service is currently running. Changes will apply after restart.
```

### 21.3 Service Details

Route:

```text
GET /Manage/Services/Details/{id}
```

Show:

- Service configuration
- Current runtime status
- Latest run
- Health check result
- Recent logs
- Run history

---

## 22. Logs Page

Routes:

```text
GET /Manage/Services/{id}/Logs
GET /Manage/Runs/{runId}/Logs
```

Features:

1. Show live logs for running service.
2. Show persisted log file for completed run.
3. Support stdout/stderr/system prefixes.
4. Auto-scroll toggle.
5. Clear live view button. This should not delete the log file.
6. Download log file button.

For v1, implement log polling:

```http
GET /Manage/Services/{id}/LogsSnapshot
```

Return JSON:

```json
{
  "serviceId": 1,
  "isRunning": true,
  "lines": [
    "2026-05-18T08:30:00+02:00 [OUT] started"
  ]
}
```

Future enhancement: SignalR streaming.

---

## 23. Launch Profiles

### 23.1 Profiles list

Route:

```text
GET /Manage/Profiles
```

Columns:

```text
Name
Description
Services Count
Is Default
Actions
```

Actions:

```text
Start
Stop
Restart
Edit
Delete
```

### 23.2 Create/Edit Profile

Fields:

```text
Name
Description
Is Default
Services
Start Order
Start Delay Seconds
```

Example profile:

```text
Full Stack Dev
1. Azurite             delay 0s
2. BAAS Function App   delay 2s
3. Local API           delay 2s
4. Frontend App        delay 0s
```

### 23.3 Start Profile behavior

When starting a profile:

1. Load profile services ordered by `StartOrder`.
2. Start each service if not already running.
3. Wait `StartDelaySeconds` after each service.
4. Capture errors but continue or stop depending on future setting.

For v1, if one service fails to start, continue starting the remaining services and show a summary.

---

## 24. Runs Page

Route:

```text
GET /Manage/Runs
```

Columns:

```text
Service
Started
Stopped
Duration
Status
Exit Code
Log File
```

Filters:

```text
Service
Status
Date range
```

Run details page:

```text
GET /Manage/Runs/Details/{id}
```

Show:

- service name
- command snapshot
- working directory snapshot
- started/stopped timestamps
- duration
- PID
- exit code
- status
- last error
- link to log file

---

## 25. Settings Page

Route:

```text
GET /Manage/Settings
POST /Manage/Settings
```

Settings:

```text
Database path
Logs folder path
Max live log lines
Stop timeout seconds
Dashboard polling interval
Auto-start enabled services on app launch
Development-only mode
```

For v1, the database and logs folder paths can be read-only display fields.

---

## 26. HTTP Endpoints

These endpoints may be MVC form posts or JSON endpoints.

### 26.1 Service actions

```http
POST /Manage/Services/{id}/Start
POST /Manage/Services/{id}/Stop
POST /Manage/Services/{id}/Restart
POST /Manage/Services/StopAll
```

### 26.2 Profile actions

```http
POST /Manage/Profiles/{id}/Start
POST /Manage/Profiles/{id}/Stop
POST /Manage/Profiles/{id}/Restart
```

### 26.3 Status endpoints

```http
GET /Manage/Status/Snapshot
GET /Manage/Services/{id}/Status
GET /Manage/Services/{id}/LogsSnapshot
```

### 26.4 Proxy route endpoints

```http
GET  /Manage/ProxyRoutes
GET  /Manage/ProxyRoutes/Create
POST /Manage/ProxyRoutes/Create
GET  /Manage/ProxyRoutes/Edit/{id}
POST /Manage/ProxyRoutes/Edit/{id}
POST /Manage/ProxyRoutes/Delete/{id}
POST /Manage/ProxyRoutes/{id}/Enable
POST /Manage/ProxyRoutes/{id}/Disable
GET  /Manage/ProxyRoutes/{id}/Test
POST /Manage/ProxyRoutes/Reload
```

Example status snapshot:

```json
{
  "services": [
    {
      "id": 1,
      "name": "BAAS Function App",
      "serviceType": "AzureFunction",
      "runtimeStatus": "Running",
      "healthStatus": "Healthy",
      "port": 7071,
      "url": "http://localhost:7071",
      "processId": 12345,
      "runId": 42
    }
  ]
}
```

---

## 27. UI Style Direction

The UI should feel like a local dev control deck.

Suggested style:

- dark sidebar or top nav
- clean service cards
- clear green/yellow/red status pills
- monospace log panel
- large action buttons for Start/Stop/Restart
- compact but readable tables
- clear direct/proxy URL links on service cards

Suggested status colors:

```text
Running       green
Starting      blue
Stopping      yellow
Stopped       gray
Crashed       red
FailedToStart red
Healthy       green
Unhealthy     red
Unknown       gray
```

No CSS framework is strictly required, but Bootstrap is acceptable for v1.

---

## 28. Example Seed Data

Create seed data only if the database is empty.

### 28.1 BAAS Azure Function

```text
Name: BAAS Function App
Type: AzureFunction
Working Directory: C:\Thrive\backend-funcs\src\FunctionAppBAAS
Command: func.cmd
Arguments: start --port {port}
Port: 7071
URL: http://localhost:7071
Health Check: http://localhost:7071
```

### 28.2 React frontend

```text
Name: Frontend App
Type: ReactVite
Working Directory: C:\Thrive\frontend
Command: npm.cmd
Arguments: run dev -- --host 0.0.0.0 --port {port}
Port: 5173
URL: http://localhost:5173
Health Check: http://localhost:5173
```

### 28.3 .NET API

```text
Name: Local API
Type: DotNetApi
Working Directory: C:\Thrive\api
Command: dotnet.exe
Arguments: run --urls http://localhost:{port}
Port: 5080
URL: http://localhost:5080
Health Check: http://localhost:5080/health
```

### 28.4 Proxy routes

```text
Name: Frontend via Proxy
Service: Frontend App
Match Path: /app/{**catch-all}
Transform: RemovePrefix /app
Proxy URL: http://localhost:5050/app
```

```text
Name: API via Proxy
Service: Local API
Match Path: /api/{**catch-all}
Transform: None
Proxy URL: http://localhost:5050/api
```

```text
Name: Functions via Proxy
Service: BAAS Function App
Match Path: /functions/{**catch-all}
Transform: RemoveAndAddPrefix /functions -> /api
Proxy URL: http://localhost:5050/functions
```

### 28.5 Full Stack profile

```text
Name: Full Stack Dev
Services:
1. BAAS Function App
2. Local API
3. Frontend App
```

---

## 29. Implementation Milestones

### Milestone 1: Project skeleton

Deliver:

- ASP.NET Core MVC app
- Manage Area
- SQLite setup
- EF Core migrations
- Basic layout
- Dashboard placeholder

Acceptance criteria:

- App runs locally.
- `/Manage` opens.
- SQLite database is created in local app data folder.

### Milestone 2: Service CRUD

Deliver:

- `DevService` entity
- service list page
- create/edit/delete service pages
- command preset provider
- environment variables
- health check definitions

Acceptance criteria:

- User can add an Azure Function service.
- User can add a React/Vite service.
- User can add a .NET API service.

### Milestone 3: Process manager

Deliver:

- singleton process manager
- start/stop/restart service
- stdout/stderr capture
- run history rows
- persistent log files
- in-memory log buffer

Acceptance criteria:

- Starting a configured service launches a real process.
- Logs are captured.
- Stop kills the process tree.
- Run history is updated on exit.

### Milestone 4: Dashboard integration

Deliver:

- service cards
- runtime status
- start/stop/restart buttons
- URL open button
- simple polling status endpoint

Acceptance criteria:

- Dashboard shows correct status changes.
- User can start and stop services from cards.

### Milestone 5: Logs UI

Deliver:

- live logs page
- polling logs endpoint
- persisted run log viewer
- download log file button

Acceptance criteria:

- User can watch output from `npm run dev`, `func start`, and `dotnet run`.

### Milestone 6: Launch profiles

Deliver:

- profile CRUD
- profile service selection
- start order
- start delay
- start profile action
- stop profile action

Acceptance criteria:

- User can start a “Full Stack Dev” profile with one button.

### Milestone 7: Health checks and ports

Deliver:

- background health checker
- port probe service
- health display on dashboard
- warnings for port conflicts

Acceptance criteria:

- Dashboard shows Healthy/Unhealthy/NotRunning.
- User gets a warning if configured port appears occupied.

### Milestone 8: Reverse proxy

Deliver:

- `Yarp.ReverseProxy` package integration
- `ProxyRoute` entity and migration
- `/Manage/ProxyRoutes` CRUD page
- dynamic `DevDeckProxyConfigProvider`
- path-based route matching
- prefix remove/add transforms
- dashboard proxy links
- proxy route validation for reserved paths

Acceptance criteria:

- React/Vite frontend can be opened through a DevDeck proxy URL.
- .NET API can be called through a DevDeck proxy URL.
- Azure Functions HTTP triggers can be called through a DevDeck proxy URL.
- `/Manage` routes are never proxied.
- Proxy route changes apply without restarting DevDeck.

### Milestone 9: Polish

Deliver:

- better UI styling
- settings page
- log retention cleanup
- better error messages
- optional auto-start services on DevDeck launch
- optional auto-start-on-proxy-request behavior

Acceptance criteria:

- DevDeck feels like a finished local dashboard.

---

## 30. Required Services and Interfaces

### 30.1 Process manager interface

```csharp
public interface IDevDeckProcessManager
{
    Task<StartServiceResult> StartServiceAsync(int serviceId, CancellationToken cancellationToken);

    Task<StopServiceResult> StopServiceAsync(int serviceId, CancellationToken cancellationToken);

    Task<RestartServiceResult> RestartServiceAsync(int serviceId, CancellationToken cancellationToken);

    Task<StartProfileResult> StartProfileAsync(int profileId, CancellationToken cancellationToken);

    Task<StopAllResult> StopAllAsync(CancellationToken cancellationToken);

    RunningProcessInfo? GetRunningProcess(int serviceId);

    IReadOnlyCollection<RunningProcessInfo> GetRunningProcesses();

    IReadOnlyList<LogLine> GetLiveLogs(int serviceId);
}
```

### 30.2 Proxy config provider interface

```csharp
public interface IDevDeckProxyConfigReloader
{
    Task ReloadProxyConfigAsync(CancellationToken cancellationToken);
}
```

The implementation can be the same concrete class as `DevDeckProxyConfigProvider`, but keeping a small app-facing interface makes controllers easier to test.

### 30.3 Result objects

```csharp
public sealed class StartServiceResult
{
    public bool Success { get; init; }

    public int ServiceId { get; init; }

    public long? RunId { get; init; }

    public string? Message { get; init; }

    public string? Error { get; init; }
}
```

Use similar patterns for stop, restart, and profile results.

---

## 31. Error Handling

The UI should display friendly errors.

Examples:

```text
Working directory does not exist.
Command could not be started.
Port 5173 appears to be in use.
Service is already running.
Service is not running.
Process exited with code 1.
```

The process manager should write system log lines for key events:

```text
[SYS] Starting process
[SYS] Process started with PID 12345
[SYS] Stop requested
[SYS] Killing process tree
[SYS] Process exited with code 0
[SYS] Failed to start: file not found
```

---

## 32. Cross-Platform Considerations

DevDeck should work best on Windows initially, but avoid hardcoding Windows-only behavior where easy.

### 32.1 Windows command examples

```text
npm.cmd
npx.cmd
func.cmd
dotnet.exe
docker.exe
```

### 32.2 Linux/macOS command examples

```text
npm
npx
func
dotnet
docker
```

### 32.3 WSL support

WSL can be a future feature.

Possible future service option:

```text
RunMode: LocalWindows | LocalUnix | Wsl
WslDistribution: Ubuntu
```

Example WSL command:

```text
wsl.exe -d Ubuntu -- bash -lc "cd /path/to/app && npm run dev"
```

Do not implement WSL mode in v1 unless explicitly requested.

---

## 33. App Configuration

Example `appsettings.Development.json`:

```json
{
  "DevDeck": {
    "DevelopmentOnly": true,
    "AutoStartEnabledServices": false,
    "StopTimeoutSeconds": 10,
    "DashboardPollingMilliseconds": 1500,
    "MaxLiveLogLinesPerService": 5000,
    "LogRetentionDays": 14,
    "ReverseProxy": {
      "Enabled": true,
      "GatewayBaseUrl": "http://localhost:5050",
      "AllowExternalDestinations": false,
      "EnableAutoStartOnRequest": false
    }
  }
}
```

Create a typed options class:

```csharp
public sealed class DevDeckOptions
{
    public bool DevelopmentOnly { get; set; } = true;

    public bool AutoStartEnabledServices { get; set; } = false;

    public int StopTimeoutSeconds { get; set; } = 10;

    public int DashboardPollingMilliseconds { get; set; } = 1500;

    public int MaxLiveLogLinesPerService { get; set; } = 5000;

    public int LogRetentionDays { get; set; } = 14;

    public DevDeckReverseProxyOptions ReverseProxy { get; set; } = new();
}

public sealed class DevDeckReverseProxyOptions
{
    public bool Enabled { get; set; } = true;

    public string GatewayBaseUrl { get; set; } = "http://localhost:5050";

    public bool AllowExternalDestinations { get; set; } = false;

    public bool EnableAutoStartOnRequest { get; set; } = false;
}
```

---

## 34. Suggested Controller Layout

### 34.1 DashboardController

```csharp
[Area("Manage")]
public sealed class DashboardController : Controller
{
    public IActionResult Index();
}
```

### 34.2 ServicesController

```csharp
[Area("Manage")]
public sealed class ServicesController : Controller
{
    public Task<IActionResult> Index();

    public IActionResult Create();

    [HttpPost]
    public Task<IActionResult> Create(ServiceEditViewModel model);

    public Task<IActionResult> Edit(int id);

    [HttpPost]
    public Task<IActionResult> Edit(int id, ServiceEditViewModel model);

    [HttpPost]
    public Task<IActionResult> Delete(int id);

    [HttpPost]
    public Task<IActionResult> Start(int id);

    [HttpPost]
    public Task<IActionResult> Stop(int id);

    [HttpPost]
    public Task<IActionResult> Restart(int id);

    public Task<IActionResult> Logs(int id);

    public IActionResult LogsSnapshot(int id);
}
```

### 34.3 ProxyRoutesController

```csharp
[Area("Manage")]
public sealed class ProxyRoutesController : Controller
{
    public Task<IActionResult> Index();

    public Task<IActionResult> Create();

    [HttpPost]
    public Task<IActionResult> Create(ProxyRouteEditViewModel model);

    public Task<IActionResult> Edit(int id);

    [HttpPost]
    public Task<IActionResult> Edit(int id, ProxyRouteEditViewModel model);

    [HttpPost]
    public Task<IActionResult> Delete(int id);

    [HttpPost]
    public Task<IActionResult> Enable(int id);

    [HttpPost]
    public Task<IActionResult> Disable(int id);

    public Task<IActionResult> Test(int id);

    [HttpPost]
    public Task<IActionResult> Reload();
}
```

### 34.4 ProfilesController

```csharp
[Area("Manage")]
public sealed class ProfilesController : Controller
{
    public Task<IActionResult> Index();

    public Task<IActionResult> Create();

    [HttpPost]
    public Task<IActionResult> Create(ProfileEditViewModel model);

    public Task<IActionResult> Edit(int id);

    [HttpPost]
    public Task<IActionResult> Edit(int id, ProfileEditViewModel model);

    [HttpPost]
    public Task<IActionResult> Start(int id);

    [HttpPost]
    public Task<IActionResult> Stop(int id);
}
```

---

## 35. Minimal CSS/UI Requirements

The UI does not need to be fancy for v1, but should be pleasant.

Minimum requirements:

1. Consistent `/Manage` layout.
2. Dashboard service cards.
3. Status badges.
4. Monospace log viewer.
5. Buttons grouped by action.
6. Tables with clear action columns.
7. Form validation messages.

Suggested log panel CSS behavior:

```text
height: 70vh;
overflow-y: auto;
font-family: monospace;
white-space: pre-wrap;
background: #111;
color: #eee;
padding: 1rem;
border-radius: 0.5rem;
```

---

## 36. Testing Requirements

### 36.1 Unit tests

Test:

- command template rendering
- command executable resolution
- process manager duplicate-start prevention
- log buffer trimming
- launch profile ordering
- port probe behavior where possible
- proxy route validation
- proxy route builder transforms
- reserved path rejection

### 36.2 Integration-ish manual tests

Create test services:

#### Test long-running dotnet command

```text
Command: dotnet
Arguments: --info
```

Expected:

- starts
- logs output
- exits
- run history shows stopped/exited

#### Test Node/Vite app

```text
Command: npm.cmd
Arguments: run dev -- --port 5173
```

Expected:

- starts
- logs output
- dashboard shows running
- stop kills process tree

#### Test reverse proxy route

```text
Service: Frontend App
Route: /app/{**catch-all}
Destination: http://localhost:5173/
Transform: RemovePrefix /app
```

Expected:

- `/app` opens through DevDeck.
- static frontend assets load.
- `/Manage` still opens DevDeck management UI.

#### Test Azure Function

```text
Command: func.cmd
Arguments: start --port 7071
```

Expected:

- starts
- logs function host output
- URL opens
- stop kills host and worker process

---

## 37. Future Enhancements

1. SignalR real-time log streaming.
2. Terminal-like interactive stdin support.
3. WSL execution mode.
4. Docker Compose parser.
5. Repository scanning for package.json, .csproj, host.json.
6. Import from Visual Studio launchSettings.json.
7. Import from VS Code tasks.json.
8. Import from Docker Compose files.
9. DPAPI or platform secret storage for environment variables.
10. Tray icon for Windows.
11. “Open in VS Code” button.
12. “Open working directory” button.
13. Dependency graph between services.
14. Per-service restart-on-crash.
15. Service groups/tags.
16. Theme customization.
17. Export/import DevDeck configuration.
18. Host-based reverse proxy routes such as `api.localhost` and `app.localhost`.
19. Local HTTPS certificates for the DevDeck gateway.
20. Proxy request history and metrics.
21. Auto-start-on-proxy-request with readiness waiting.

---

## 38. Build Priorities for Coding Agent

Build in this order:

1. MVC app with `/Manage` area.
2. SQLite entities and migrations.
3. Service CRUD.
4. Process manager.
5. Dashboard actions.
6. Live log view.
7. Launch profiles.
8. Health checks.
9. Reverse proxy.
10. Styling and polish.

Avoid spending time on advanced features before the core process runner works.

The most important early proof is:

```text
Create service -> Start service -> See logs -> Open through proxy -> Stop service -> See run history
```

---

## 39. Definition of Done for v1

DevDeck v1 is done when:

1. The user can add services through `/Manage/Services`.
2. The user can start `func start` for an Azure Function app.
3. The user can start `npm run dev` or `npm start` for a React app.
4. The user can start `dotnet run` for a .NET API.
5. The dashboard shows whether each service is running.
6. The user can stop and restart running services.
7. Logs stream into a browser view.
8. Logs are also written to files.
9. Run history is saved in SQLite.
10. A launch profile can start multiple services with one button.
11. Health checks show service health.
12. The user can configure a reverse proxy route for a React/Vite app.
13. The user can configure a reverse proxy route for a .NET API.
14. The user can configure a reverse proxy route for an Azure Function app.
15. Reverse proxy route changes apply without restarting DevDeck.
16. The UI is usable and clear.

---

## 40. Suggested Taglines

```text
DevDeck — One dashboard. Every local dev service.
```

```text
DevDeck — Launch your stack without the terminal circus.
```

```text
DevDeck — Start, stop, stream logs, survive localhost.
```

---

## 41. Reference Documentation

These are useful implementation references for the coding agent:

- [YARP overview](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/yarp-overview?view=aspnetcore-10.0)
- [YARP configuration files](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-files?view=aspnetcore-10.0)
- [YARP configuration providers](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-providers?view=aspnetcore-10.0)
- [YARP request transforms](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/transforms-request?view=aspnetcore-10.0)
- [YARP authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/authn-authz?view=aspnetcore-10.0)

---

## 42. Final Notes for Implementation

Keep DevDeck simple and reliable first.

The heart of the application is not the UI. The heart is the process manager.

The process manager must reliably:

1. Start processes.
2. Capture output.
3. Stop process trees.
4. Record run history.
5. Keep live status accurate.

Once that works, the rest of DevDeck becomes a friendly control panel around it.

The reverse proxy is the second major pillar. It should make the running services feel like one local stack behind one gateway URL, while keeping `/Manage` safely reserved for DevDeck itself.

