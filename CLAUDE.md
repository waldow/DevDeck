# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
It is kept in sync with `AGENTS.md` (same content, different audience line) — update both together.

## What DevDeck is

A local-developer-only ASP.NET Core MVC dashboard (`.NET 10`) that supervises the processes a project
needs — Azure Functions, Vite/CRA frontends, .NET/Node APIs, Docker Compose, custom commands — and exposes
them behind one YARP reverse-proxy origin. From one browser tab you can start/stop/restart services, stream
logs, watch health, and route `http://localhost:5050/app`, `/api`, … to the right local port.

`README.md` is the user-facing manual (quick start, features, configuration, safety model). This file is the
**contributor/agent** guide: architecture, invariants, and conventions to preserve when changing code.

## Repository layout

- `DevDeck.slnx` — solution. Two projects, both `net10.0`:
  - `DevDeck.Web` — the application (only deps: `Microsoft.EntityFrameworkCore.Sqlite`, `Yarp.ReverseProxy`).
  - `DevDeck.Tests` — xUnit + FluentAssertions unit tests (currently **108**, all green).
- `DevDeck_Specification_v2_Reverse_Proxy.md` — the original design spec. **Historical**: v1 is fully built,
  so this is no longer a build target. Its section numbers (e.g. §8 entities, §18 proxy) are still useful as
  rationale when a change touches a documented decision — cite them, but the code is the source of truth.
- `DevDeck.Web` internals:
  - `Areas/Manage` — the entire UI. Controllers: `Dashboard`, `Services`, `ProxyRoutes`, `Profiles`, `Runs`,
    `Settings`, `Logs`, and `Status` (the JSON polling endpoint). Plus `ViewModels/` and Razor `Views/`.
  - `Controllers/Home` — minimal public site (uses Bootstrap; the Manage area does **not**).
  - `Data/` — `DevDeckDbContext`, `DevDeckPaths`, and `Entities/` (`DevService`, `ServiceEnvironmentVariable`,
    `ServiceHealthCheck`, `ServiceRun`, `LaunchProfile`, `LaunchProfileService`, `ProxyRoute`, `AppSetting`).
  - `Migrations/` — EF Core migrations (initial: `20260518075931_Initial`).
  - `Options/DevDeckOptions.cs` — all configuration (`DevDeck` config section).
  - `Services/{Commands,Health,Logs,Portability,Proxy,Runtime}` — the engine (see below).
  - `wwwroot/` — `css/devdeck.css` (the design system) and `js/` polling scripts.

## Architecture — the three-tier state separation (core invariant)

This separation is the single most important rule; preserve it across all changes:

```
SQLite     -> persistent configuration + run summaries only  (never full log streams)
Memory     -> live Process handles, RunningProcessInfo, log ring buffer, YARP snapshot
Log files  -> durable stdout/stderr, one file per ServiceRun under <data>/logs/
```

Key singletons registered in `Program.cs`:
- `DevDeckProcessManager` (`IDevDeckProcessManager`) — owns the running-process map; prevents duplicate
  starts; start/stop/restart/start-all/stop-all; process-tree kill on stop (10s timeout then
  `Process.Kill(entireProcessTree: true)`).
- `ProcessLogBuffer` + `LogFileWriter` — dual-write each log line to the in-memory ring (5000 lines/service,
  trim 1000) **and** to disk.
- `RunHistoryRefreshService` — reconciles `ServiceRun` rows when processes exit out-of-band.
- `DevDeckProxyConfigProvider : IProxyConfigProvider` — builds YARP route/cluster snapshots in memory from
  `ProxyRoute` rows and exposes a change token for hot reload. **Must not query SQLite per proxied request.**
- `HealthCheckBackgroundService` (hosted) + `HealthStatusCache` — poll enabled `ServiceHealthCheck` URLs.
- `PortProbeService` — TCP-probes `127.0.0.1:{port}` to detect conflicts.
- `AzuriteSupervisor` (`IAzuriteSupervisor`) — see below.
- `AutoStartHostedService` (hosted) — on boot, starts enabled + `AutoStart` services in `DisplayOrder` (only
  when `AutoStartEnabledServices` is set).
- `PortabilityExporter` / `PortabilityImporter` — JSON import/export.

## Request pipeline & ordering invariants (`Program.cs`)

Order matters and is load-bearing:
1. On startup: `db.Database.MigrateAsync()` then `proxyProvider.ReloadAsync()`.
2. Kestrel binds to `GatewayUrlResolver.ResolveListenUrl(config)` (gateway default `http://localhost:5050`).
3. `UseStaticFiles` → `UseRouting` → (DevelopmentOnly 404 guard for `/Manage` outside Development) →
   `UseAuthorization`.
4. **MVC routes are mapped BEFORE `app.MapReverseProxy()`** so `/Manage` always wins. Do not reorder.
5. `MapReverseProxy` runs only when `ReverseProxy.Enabled`, and every proxied request first passes
   `ProxyRequestGuard.AllowRequestAsync`.
6. `MapGet("/")` → redirect to `/Manage` with `WithOrder(int.MaxValue)` so an explicit catch-all route can
   take precedence.

## Non-negotiable safety constraints

DevDeck runs arbitrary processes and exposes a reverse proxy, so these are security invariants — violating one
is a regression even if it compiles:

1. **No raw command-execution endpoint.** Every start goes through a stored, validated `DevService` row.
   There is no endpoint that accepts a command string.
2. **`/Manage` (case-insensitive) and other reserved prefixes** (`/css`, `/js`, `/lib`, `/images`,
   `/favicon.ico`, `/_devdeck`) must never be reachable through a proxy route — enforced by `ReservedPaths`
   + `ProxyDestinationValidator` at config time and `ProxyRequestGuard` at request time.
3. **Catch-all routes are disabled** unless `ReverseProxy.AllowCatchAllRoutes` is set.
4. **Proxy destinations default to localhost / 127.0.0.1 / `*.localhost` / private networks only.** Public
   destinations require `ReverseProxy.AllowExternalDestinations = true`.
5. **MVC before `MapReverseProxy()`** (see pipeline above).
6. **`DevelopmentOnly` guard** (default true) 404s `/Manage` outside the Development environment.
7. **Secrets** (`ServiceEnvironmentVariable.IsSecret`) are masked in the UI (`EnvVarEditRow.SecretPlaceholder`)
   and never logged. SQLite storage of the value is acceptable for v1.

## Front-end conventions (the live UI)

- The Manage area uses `Areas/Manage/Views/Shared/_ManageLayout.cshtml` and the self-contained
  `wwwroot/css/devdeck.css` design system — a dark "control deck" aesthetic with CSS variables, status pills,
  and ignite/`online-pop` animations. **No Bootstrap, no external CSS, no web fonts** in the Manage area; keep
  it dependency-free and respect `prefers-reduced-motion` (existing animations already do).
- **Live status is poll-based, not SignalR.** Pages poll `GET /Manage/Status/Snapshot` (interval =
  `DashboardPollingMilliseconds`). The contract scripts depend on:
  - container with `data-poll`, rows/cards with `data-service-id` and `data-enabled`,
  - status pills marked `data-field="runtime"` / `data-field="health"`, classed `pill-{status-lowercased}`.
  - `wwwroot/js/dashboard.js` (cards) and `wwwroot/js/services.js` (table) implement this;
    `start-all.js` drives the staggered launch cascade; `logs.js` streams `GET /Manage/Services/{id}/LogsSnapshot`.
  When adding a live surface, reuse these data attributes and the snapshot endpoint rather than inventing a new
  channel. Start/Stop/Restart actions on `ServicesController` return JSON when the caller sends
  `X-Requested-With: XMLHttpRequest` / `Accept: application/json`, enabling in-place AJAX updates.

## Cross-platform command resolution & templating

- `CommandExecutableResolver`: Windows maps `npm`→`npm.cmd`, `func`→`func.cmd`, `dotnet`→`dotnet.exe`, etc.
  (preferring PATHEXT-launchable shims); Linux/macOS strips `.cmd`/`.exe`; absolute paths pass through.
- `CommandTemplateRenderer`: arguments support `{id}`, `{name}`, `{port}`, `{workingDirectory}`. Unknown
  placeholders are left intact (with a UI warning), not silently emptied.
- `CommandPresetProvider`: presets (Azure Function, React/Vite, React/CRA, Node API, .NET API, Docker Compose,
  Custom) with default ports — Functions 7071, Vite 5173, CRA 3000, Node API 3001, .NET API 5080.

## Azurite & auto-start

`AzuriteSupervisor` ensures the Azurite storage emulator is listening before an Azure Functions host starts
(the Functions runtime needs `AzureWebJobsStorage`). If Azurite's ports are already up it is reused; otherwise
DevDeck launches the global `azurite` CLI as a managed background process and waits for its ports. Configurable
under `DevDeck:Azurite` (command, blob/queue/table ports, startup timeout).

## Passthru / external-instance mode

A service can be flipped to **passthru** mode (`DevService.UseExternalInstance` + `DevService.ExternalPort`,
default 7071) so DevDeck stops launching/managing the process and instead proxies to, health-checks, and reports
the status of an instance the developer runs themselves (e.g. a Functions host under the Visual Studio debugger).
The single mechanism is `DevService.EffectivePort` (`= UseExternalInstance ? (ExternalPort ?? Port) : Port`): the
proxy destination (`ProxyRouteBuilder.ResolveDestination`), health-check URL (`HealthCheckBackgroundService`), and
status probe (`StatusController`) all render `{port}` from it, so flipping the switch repoints the whole stack
coherently — toggling only needs a YARP `ReloadAsync()`. Passthru services are skipped by `StartServiceAsync`,
`StartAllAsync`, and `AutoStartHostedService`; `StatusController.Snapshot` TCP-probes the external port and reports
`External`/`Offline` instead of the managed `Running`/`Stopped`. Toggle via the Edit form or the one-click
`POST /Manage/Services/{id}/ToggleExternal` (which defaults `ExternalPort` to `Port ?? 7071` and reloads the proxy).

## Portability (import / export)

`PortabilityExporter` / `PortabilityImporter` round-trip services and proxy routes as JSON. Foreign references
use **names** (not ids) so files are portable across machines; importing updates same-named rows in place and
creates new ones. Secrets are excluded unless `includeSecrets` is requested. The `_ImportExportToolbar`
partial provides the UI. `devdeck-main-react-api-routes.json` at the repo root is an example export.

## Configuration (`DevDeck` section, see `DevDeckOptions`)

Defaults: `DevelopmentOnly=true`, `AutoStartEnabledServices=false`, `StopTimeoutSeconds=10`,
`DashboardPollingMilliseconds=1500`, `MaxLiveLogLinesPerService=5000`, `LogTrimAmount=1000`,
`LogRetentionDays=14`. `ReverseProxy`: `Enabled=true`, `GatewayBaseUrl=http://localhost:5050`,
`AllowExternalDestinations=false`, `AllowCatchAllRoutes=false`, `EnableAutoStartOnRequest=false`,
`LogProxyRequests=true` (each proxied request writes a `PRX` inbound/outbound line pair into the
target service's log stream via `ProxyRequestLogger`).

## Storage paths

`DevDeckPaths` resolves `Environment.SpecialFolder.LocalApplicationData / DevDeck/` — `~/.local/share/DevDeck/`
on Linux/WSL, `%LOCALAPPDATA%\DevDeck\` on Windows. Holds `devdeck.db` and `logs/` (one `.log` per run).

## Build / test / run

```
dotnet build                                                          # build the solution (DevDeck.slnx)
dotnet test                                                           # run all unit tests (108)
dotnet run --project DevDeck.Web                                      # launch on http://localhost:5050
dotnet ef migrations add <Name> --project DevDeck.Web -o Migrations   # new EF migration
```

The database is created/migrated automatically on startup, so no manual `database update` step is needed to run.
