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
  - `DevDeck.Tests` — xUnit + FluentAssertions unit tests (currently **342**, all green). Some exercise real
    processes (POSIX process groups/signals; one Windows-only Ctrl+C test) — run the suite on both OSes when
    touching `Services/Runtime`.
- `DevDeck_Specification_v2_Reverse_Proxy.md` — the original design spec. **Historical**: v1 is fully built,
  so this is no longer a build target. Its section numbers (e.g. §8 entities, §18 proxy) are still useful as
  rationale when a change touches a documented decision — cite them, but the code is the source of truth.
- `DevDeck.Web` internals:
  - `Areas/Manage` — the entire UI. Controllers: `Dashboard`, `Services`, `ProxyRoutes`, `Profiles`, `Runs`,
    `Settings`, `Logs`, and `Status` (the JSON polling endpoint). Plus `ViewModels/` and Razor `Views/`.
  - `Controllers/Home` — minimal public site (uses Bootstrap; the Manage area does **not**).
  - `Data/` — `DevDeckDbContext`, `DevDeckPaths`, and `Entities/` (`DevService`, `ServiceEnvironmentVariable`,
    `ServiceHealthCheck`, `ServiceRun`, `LaunchProfile`, `LaunchProfileService`, `ProxyRoute`, `AppSetting`).
  - `Migrations/` — EF Core migrations (initial: `20260518075931_Initial`; latest: `AddProcessStartKey`).
  - `Options/DevDeckOptions.cs` — all configuration (`DevDeck` config section).
  - `Services/{Commands,Health,Logs,Portability,Proxy,Runtime}` — the engine (see below).
  - `wwwroot/` — `css/devdeck.css` (the design system), `js/` polling scripts, `images/` (logo and icons).
    Nothing but `favicon.ico` sits at the web root: see safety constraint 2.

## Architecture — the three-tier state separation (core invariant)

This separation is the single most important rule; preserve it across all changes:

```
SQLite     -> persistent configuration + run summaries only  (never full log streams)
Memory     -> live Process handles, RunningProcessInfo, log ring buffer, YARP snapshot
Log files  -> durable stdout/stderr, one file per ServiceRun under <data>/logs/
```

Key singletons registered in `Program.cs`:
- `DevDeckProcessManager` (`IDevDeckProcessManager`) — owns the running-process map; prevents duplicate
  starts; start/stop/restart/start-all/stop-all/profile start. Lifecycle operations ignore the caller's
  token (an aborted request must not leave a start or stop half-done); every wait is bounded instead.
  Stop (`ProcessTerminator`) works on the **whole tree**: it snapshots the descendants first (`ProcessTree`),
  runs the service's optional `StopCommand`, then sends a graceful signal — SIGTERM to the service's own
  process group (Linux services are started via `setsid`, see `ProcessSessions`) or to every descendant;
  Ctrl+C on Windows via a helper copy of DevDeck (`WindowsConsoleSignal`, the `--devdeck-send-ctrl-c`
  mode at the top of `Program.cs`) — and after `StopTimeoutSeconds` force-kills whatever is left, including
  descendants that outlived the root. Shared build servers in the tree (VBCSCompiler, MSBuild `nodeReuse` workers,
  the Razor server — `ProcessTree.IsSharedBuildServer`, by command line) are left alone: `dotnet run` leaves them
  behind on purpose and every build of the user's uses them. setsid is only used for a file the kernel can exec
  directly (ELF, or a `#!` interpreter that exists), so a command that can't run still fails as FailedToStart.
  The final run status is decided in the exit handler from in-memory
  state captured at the moment of exit (a Stop that lands after a crash doesn't relabel it).
  `AdoptOrphanedRunsAsync` (called at startup, and by Start for its own service) re-attaches processes an
  earlier DevDeck session left running — recognised by `ServiceRun.ProcessStartKey` (the kernel's start tick on
  Linux, which a wall-clock step doesn't move; `RunProcessMatcher`), falling back to the start-time window for
  older runs. Re-attached runs (`RunningProcessInfo.IsAdopted`) show as running, are proxied to, can be stopped,
  and are never launched twice.
- `ProcessLogBuffer` + `LogFileWriter` — dual-write each log line to the in-memory ring (5000 lines/service,
  trim 1000) **and** to disk.
- `RunHistoryRefreshService` — reconciles `ServiceRun` rows when processes exit out-of-band. Runs the process
  manager still tracks (or has a start/stop in flight for — `IsServiceBusy`) belong to the manager's exit
  handler: for those it only fills in Starting→Running / the PID, with a conditional update that can never land
  on a stopping or finished row; the handler itself waits for the start sequence to finish before finalizing.
- `DevDeckProxyConfigProvider : IProxyConfigProvider` — builds YARP route/cluster snapshots in memory from
  `ProxyRoute` rows and exposes a change token for hot reload. **Must not query SQLite per proxied request.**
  `ProxyRouteBuilder.BuildAsync` also runs each route through YARP's own `IConfigValidator` and drops invalid
  ones with a warning — YARP rejects a whole snapshot (and throws at startup) if any single route is invalid.
  Routing builds **one** matcher over every endpoint (MVC's `/Manage` included), so the builder also refuses
  what only that build would catch — Match hosts `HostMatcherPolicy` can't parse (checked with the real policy)
  and constraints `ParameterPolicyFactory` can't resolve — plus the `Default` authorization policy (DevDeck has
  no authentication scheme: every request would 500), destinations on the gateway itself (a loop), timeouts
  below 1s, and a route ambiguous with an earlier one (`ProxyRouteConflicts`: same template ignoring parameter
  names, same Order, overlapping hosts → `AmbiguousMatchException`). The editor and importer run the same
  checks and refuse such routes up front. `ReloadAsync` is serialized.
- `HealthCheckBackgroundService` (hosted) + `HealthStatusCache` — poll enabled `ServiceHealthCheck` URLs. The
  cache is the single source of health: the `RequireHealthyDestination` gate *and* every UI pill read its
  aggregate (any Unhealthy wins; `Warming` during the 15s post-start window). A service with no enabled check
  passes the gate. Due-ness comes from the in-memory results, so a restarted service is re-checked on the next pass.
- `PortProbeService` — TCP-probes `127.0.0.1:{port}` and `[::1]:{port}` concurrently (a dev server bound to
  `localhost` may listen on IPv6 only) to detect conflicts and passthru/readiness state.
- `AzuriteSupervisor` (`IAzuriteSupervisor`) — see below.
- `AutoStartHostedService` (hosted `BackgroundService`) — once the server is listening (`ApplicationStarted`),
  starts enabled + `AutoStart` services in `DisplayOrder` (only when `AutoStartEnabledServices` is set), each in
  its own try/catch. Never from `StartAsync`: the web server only binds after every hosted service's `StartAsync`.
- `PortabilityExporter` / `PortabilityImporter` — JSON import/export.
- `LogRetentionService` (hosted) — deletes run log files older than `LogRetentionDays` (12h sweep; `<= 0`
  disables), never the log of an active run, and clears `ServiceRun.LogFilePath` of the runs whose log it deleted
  (so the UI stops offering a download; `LogsController.Download` explains a missing file instead of a 404).
- `StopServicesOnShutdownHostedService` (`IHostedLifecycleService`) — when `StopServicesOnShutdown` is on (the default),
  stops all managed services in `StoppingAsync`, i.e. *before* Kestrel drains its connections (a proxied HMR
  websocket would otherwise use up the shared shutdown token first). It always calls `BeginShutdown()` first:
  from then on no start launches anything (Stop-all also waits out starts in flight), so an auto-start or request
  in progress can't launch a service behind the shutdown. With the option off, services are left running and
  re-attached on the next start (as they are after DevDeck is killed or crashes) — but a child's stdout/stderr are
  pipes DevDeck owns, so after DevDeck exits a service that writes output (Node dev servers, Go tools) dies on its
  next line (EPIPE/SIGPIPE); .NET hosts and quiet processes keep running. That is why the default is on.
- `AllowedHostsPolicy` — the `AllowedHosts` host-filtering list (see safety constraint 8); the route editor
  warns about Match hosts it would reject.

## Request pipeline & ordering invariants (`Program.cs`)

Order matters and is load-bearing:
0. `args` = `--devdeck-send-ctrl-c <pid>` → Windows Ctrl+C helper mode; runs and exits before anything else.
1. On startup: `db.Database.MigrateAsync()` then `proxyProvider.ReloadAsync()`, then re-attach services an earlier
   session left running (`AdoptOrphanedRunsAsync`) and close out runs whose process died
   (`RefreshActiveRunsAsync`).
2. Kestrel binds to `GatewayUrlResolver.ResolveListenUrl(config)` (gateway default `http://localhost:5050`).
3. `UseStaticFiles` → `UseRouting` → (DevelopmentOnly 404 guard for `/Manage` outside Development) →
   `UseAuthorization`.
4. **MVC routes are mapped BEFORE `app.MapReverseProxy()`** so `/Manage` always wins. Do not reorder.
5. `MapReverseProxy` runs only when `ReverseProxy.Enabled`, and every proxied request first passes
   `ProxyRequestGuard.AllowRequestAsync`. Mapping order alone does not make MVC win — endpoint routing ranks
   by `Order` first — so `ReservedPathMatcherPolicy` (a routing `MatcherPolicy`) removes proxy endpoints from
   the candidates of any request under a reserved prefix.
6. `MapGet("/")` → redirect to `/Manage` with `WithOrder(int.MaxValue)` so an explicit catch-all route can
   take precedence.

## Non-negotiable safety constraints

DevDeck runs arbitrary processes and exposes a reverse proxy, so these are security invariants — violating one
is a regression even if it compiles:

1. **No raw command-execution endpoint.** Every start goes through a stored, validated `DevService` row.
   There is no endpoint that accepts a command string.
2. **`/Manage` (case-insensitive) and other reserved prefixes** (`/css`, `/js`, `/lib`, `/images`,
   `/favicon.ico`, `/_devdeck`) must never be reachable through a proxy route — enforced by `ReservedPaths`
   + `ProxyDestinationValidator` at config time and by `ReservedPathMatcherPolicy` (with a
   `ProxyRequestGuard` backstop) at request time, whatever the route's `Order`. Conversely, every static asset
   must live under a reserved prefix: `UseStaticFiles` runs before routing, so a file at the web root would
   silently shadow a proxied app's path (`StaticAssetPlacementTests` enforces this). Proxy routes carry no
   gateway-side request body limit (`MaxRequestBodySize = -1`): the service enforces its own.
3. **Catch-all routes are disabled** unless `ReverseProxy.AllowCatchAllRoutes` is set. `ReservedPaths` judges
   the parsed template: any match path with no literal text (`/`, `/{**path}`, `/{x}/{**rest}`) counts as a
   catch-all, whatever its parameters are named.
4. **Proxy destinations default to localhost / 127.0.0.1 / `*.localhost` / private networks only.** Public
   destinations require `ReverseProxy.AllowExternalDestinations = true`.
5. **MVC before `MapReverseProxy()`** (see pipeline above).
6. **`DevelopmentOnly` guard** (default true) 404s `/Manage` outside the Development environment.
7. **Secrets** (`ServiceEnvironmentVariable.IsSecret`) are masked in the UI (`EnvVarEditRow.SecretPlaceholder`)
   and never logged. SQLite storage of the value is acceptable for v1.
8. **Host filtering.** DevDeck has no authentication, so it only answers requests addressed to allowed host
   names (`AllowedHosts`, default `localhost;*.localhost;127.0.0.1;[::1]`; `AllowedHostsPolicy.IncludeDevDeckHosts`
   always adds the loopback names and a concrete gateway host). This is what stops DNS rebinding (a hostile page
   whose name re-resolves to 127.0.0.1) from driving `/Manage`. Never ship `"AllowedHosts": "*"` (DevDeck logs a
   warning if it sees it).

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
    `start-all.js` drives the staggered launch cascade; `logs.js` streams `GET /Manage/Services/{id}/LogsSnapshot`
    using a `since` sequence cursor (not a line count — the ring buffer trims its front).
  When adding a live surface, reuse these data attributes and the snapshot endpoint rather than inventing a new
  channel. Start/Stop/Restart actions on `ServicesController` return JSON when the caller sends
  `X-Requested-With: XMLHttpRequest` / `Accept: application/json`, enabling in-place AJAX updates.
- **Editors never fail silently.** The Service/Profile/Route edit views render `asp-validation-summary="All"`
  (there is no client-side validation script), so every error — including row-level and model-level ones — is
  visible. `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is on (blank editor rows bind as
  null and are skipped, not "required"); the controllers clear ModelState for rows that won't be saved (removed
  with ✕, blank, unticked profile rows); re-rendered rows keep their `Delete` flag (`row-removed`). Don't add
  HTML5 `min`/`max` to inputs that can be hidden (passthru-only fields, removable rows): the browser would block
  the submit on a control the user can't see.
- **Import messages in TempData are capped** (`PortabilityImportResult.ToTempDataMessages`): TempData is a cookie,
  and an oversized one turns every later request into HTTP 431.

## Cross-platform command resolution & templating

- `CommandExecutableResolver`: Windows maps `npm`→`npm.cmd`, `func`→`func.cmd`, `dotnet`→`dotnet.exe`, etc.
  (preferring PATHEXT-launchable shims); Linux/macOS strips `.cmd`/`.exe`; absolute paths pass through.
- `CommandExecutableResolver.ResolveForLaunch` also: falls back from a missing npm-style shim to the PATHEXT
  search (MSI-installed Core Tools ship only `func.exe`), and resolves a relative command (`./run.sh`) against
  the service's working directory, not DevDeck's.
- `CommandTemplateRenderer`: arguments support `{id}`, `{name}`, `{port}`, `{workingDirectory}`. Unknown
  placeholders are left intact (with a UI warning), not silently emptied. `${NAME}` is never a placeholder
  (shell syntax). `RenderArguments` (used for Start/Stop arguments) quotes or escapes values with spaces/quotes
  per the `ProcessStartInfo.Arguments` splitting rules (`CommandLine`); env vars and URLs use plain `Render`.
- Services don't inherit DevDeck's own `ASPNETCORE_URLS`/`*_HTTP_PORTS` (they'd bind DevDeck's URL) unless they
  set them.
- `CommandPresetProvider`: presets (Azure Function, React/Vite, React/CRA, Node API, .NET API, Docker Compose,
  Custom) with default ports — Functions 7071, Vite 5173, CRA 3000, Node API 3001, .NET API 5080. Docker Compose
  also sets the stop command `docker compose stop` (stopping the attached `compose up` client alone can leave
  the containers running).

## Azurite & auto-start

`AzuriteSupervisor` ensures the Azurite storage emulator is listening before an Azure Functions host starts
(the Functions runtime needs `AzureWebJobsStorage`). If Azurite's ports are already up it is reused; otherwise
DevDeck launches the global `azurite` CLI as a managed background process and waits for its ports. Configurable
under `DevDeck:Azurite` (command, blob/queue/table ports, startup timeout). If the launched Azurite exits during
startup (e.g. a port already taken) the wait ends at once with that error instead of running out the timeout. On DevDeck shutdown a DevDeck-launched
Azurite follows the Functions hosts that need it: stopped with them under `StopServicesOnShutdown` (the default),
otherwise left running (and reused on the next start).

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
creates new ones. Secrets are excluded unless `includeSecrets` is requested. Route imports apply the same
safety checks as the route editor (`ReservedPaths` + `ProxyDestinationValidator`, then YARP validation via
`ProxyRouteBuilder.ValidateAsync`) and skip violating rows
with an error in the result. The `_ImportExportToolbar`
partial provides the UI. `devdeck-main-react-api-routes.json` at the repo root is an example export.

## Configuration (`DevDeck` section, see `DevDeckOptions`)

Defaults: `DevelopmentOnly=true`, `AutoStartEnabledServices=false`, `StopTimeoutSeconds=10` (budget for the stop
command, then again as the grace period before force-kill),
`DashboardPollingMilliseconds=1500`, `MaxLiveLogLinesPerService=5000`, `LogTrimAmount=1000`,
`LogRetentionDays=14` (enforced by `LogRetentionService`), `StopServicesOnShutdown=true`.
`ReverseProxy`: `Enabled=true`, `GatewayBaseUrl=http://localhost:5050` (a non-loopback host logs a startup
warning — DevDeck has no authentication),
`AllowExternalDestinations=false`, `AllowCatchAllRoutes=false`, `EnableAutoStartOnRequest=false`,
`LogProxyRequests=true` (each proxied request writes a `PRX` inbound/outbound line pair into the
target service's log stream via `ProxyRequestLogger`). Top level (not under `DevDeck`):
`AllowedHosts=localhost;*.localhost;127.0.0.1;[::1]`.

## Storage paths

`DevDeckPaths` resolves `Environment.SpecialFolder.LocalApplicationData / DevDeck/` — `~/.local/share/DevDeck/`
on Linux/WSL, `%LOCALAPPDATA%\DevDeck\` on Windows. Holds `devdeck.db` and `logs/` (one `.log` per run).

## Build / test / run

```
dotnet build                                                          # build the solution (DevDeck.slnx)
dotnet test                                                           # run all unit tests (342)
dotnet run --project DevDeck.Web                                      # launch on http://localhost:5050
dotnet ef migrations add <Name> --project DevDeck.Web -o Migrations   # new EF migration
```

The database is created/migrated automatically on startup, so no manual `database update` step is needed to run.
