<div align="center">

<img src="DevDeck.Web/wwwroot/images/devdeck-logo.png" alt="DevDeck logo" width="168" />

# DevDeck

### Your local stack, under one glowing control deck.

DevDeck is a local-developer dashboard for running the whole messy orchestra: Azure Functions, Vite frontends,
.NET APIs, Node services, Docker Compose, custom commands, and whatever else your project needs. Configure once,
then start, stop, restart, stream logs, watch health, and route everything through **one reverse-proxy origin**.

<br/>

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core MVC](https://img.shields.io/badge/ASP.NET_Core-MVC-512BD4?logo=dotnet&logoColor=white)
![YARP](https://img.shields.io/badge/Reverse_Proxy-YARP-0078D4)
![SQLite](https://img.shields.io/badge/Storage-SQLite-003B57?logo=sqlite&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%2FWSL-success)
![License: MIT](https://img.shields.io/badge/License-MIT-green)

<br/>

**One browser tab. One gateway. Every service in reach.**

<br/>

[Quick start](#-quick-start) ·
[Features](#-features) ·
[Reverse proxy](#-reverse-proxy) ·
[Import / Export](#-import--export) ·
[Configuration](#%EF%B8%8F-configuration) ·
[Safety model](#-safety-model)

</div>

---

<table>
<tr>
<td width="64%" valign="middle">

## The local-dev command center

DevDeck turns terminal sprawl into a dashboard built for everyday development. It keeps process supervision,
logs, health checks, route configuration, and import/export close together, while the gateway at
`http://localhost:5050` makes your app stack feel like one coherent origin.

</td>
<td width="36%" align="center" valign="middle">
<img src="DevDeck.Web/wwwroot/images/devdeck-icon.png" alt="DevDeck app icon" width="220" />
</td>
</tr>
</table>

---

## 💡 Why

Local development usually means juggling six terminals:

```text
Terminal 1: cd backend  && func start --port 7071
Terminal 2: cd frontend && npm run dev -- --port 5173
Terminal 3: cd api      && dotnet run --urls http://localhost:5080
Terminal 4: docker compose up
Terminal 5: tail -f logs/...
Terminal 6: trying to remember which one to Ctrl+C
```

DevDeck collapses that into one UI. Logs stream into a single panel. Health pills tell you what's actually up. A built-in reverse proxy at `http://localhost:5050` makes your frontend, API, and functions **share an origin** — so CORS stops being a daily nuisance and cookies behave consistently across services.

---

## ✨ Features

<table>
<tr>
<td width="50%" valign="top">

**🟢 Process supervision**
- Start, stop, restart, and watch any local command (`npm run dev`, `func start`, `dotnet run`, `docker compose up`, custom binaries).
- **Start all / Stop all** from the dashboard — a staggered ignite / power-down cascade animates cards as they come up and go down.
- Per-service environment variables, with **secret masking** in the UI.
- **Whole-tree stop** — `npm` and everything under it go down together: a graceful signal first (SIGTERM to the service's own process group on Linux/macOS, Ctrl+C on Windows), an optional **stop command** (e.g. `docker compose stop`), then a force-kill of anything still running after `StopTimeoutSeconds`.
- Services are stopped when DevDeck shuts down; any it leaves behind (it was killed, or you turned `StopServicesOnShutdown` off) are **re-attached** on its next start — shown as running, proxied to, stoppable, never launched twice.
- Run history per service: start/stop timestamps, exit codes, downloadable logs.

**📦 Launch profiles**
- Group services into named profiles ("Full Stack Dev", "Frontend Only").
- **Ordered start** with per-service start delays.

**📜 Live + persistent logs**
- 5,000-line in-memory ring buffer per service, auto-scrolling monospace panel.
- **VS Code-style semantic coloring** — log levels, HTTP verbs, status codes, durations, versions, file paths, and clickable URLs are tinted as they stream.
- Every line also written to `{slug}-{run}-{timestamp}.log` for archival.
- `[OUT]` / `[ERR]` / `[SYS]` / `[PRX]` stream tagging.

</td>
<td width="50%" valign="top">

**❤️ Health & port awareness**
- HTTP health checks on a background interval, with `Healthy` / `Unhealthy` / `Timeout` / `NotRunning` states (`Warming` for the first 15 s after a start).
- One health verdict everywhere: the pills show exactly what a "require healthy destination" route checks (all of a service's checks; a service with no checks is never blocked).
- Port probes warn when a configured port is already in use.

**🔀 Reverse proxy (YARP) — first class**
- Path-based routing: `/app → :5173`, `/api → :5080`, `/functions → :7071/api`.
- Transforms: `None`, `RemovePrefix`, `AddPrefix`, `RemoveAndAddPrefix`, `SetPath`.
- Reserved paths can't be hijacked; **hot-reload** on every route edit.

**🔁 Passthru to your own instance**
- Flip any service to **external (passthru)** mode with one click — DevDeck stops launching it and instead
  proxies to, health-checks, and reports the status of an instance you run yourself (e.g. a Functions host
  under the Visual Studio debugger) on its dev port (7071 by default).
- The card shows a cyan **External** pill when your instance is up, **Offline** when it isn't.

**🔁 Import / Export**
- Move services, profiles, and routes between machines as portable JSON.
- Keyed by name, secrets masked by default.

**🖥️ Cross-platform**
- `npm` → `npm.cmd` on Windows, `npm` on Linux/macOS — automatic.
- Data under `%LOCALAPPDATA%\DevDeck` (Windows) or `~/.local/share/DevDeck` (Linux/WSL).

</td>
</tr>
</table>

---

## ⚡ Quick start

> **Prerequisite:** the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
git clone <your-fork-or-repo-url>
cd DevDeck
dotnet run --project DevDeck.Web
```

Then open **<http://localhost:5050>**.

DevDeck migrates its SQLite database on first launch and starts with an empty dashboard. Click **+ New service**, pick a preset, point it at a working directory, and hit **Start**. The earliest proof-of-life loop is:

```text
Create service → Start → See logs → Stop → See run history
```

> 💡 To run on a different port, change `DevDeck:ReverseProxy:GatewayBaseUrl` — the gateway binds Kestrel to exactly that URL.

---

## 🧩 Service presets

When you create a service, pick a preset to pre-fill the command, default port, URL, and health check. Every field stays editable, and arguments support the placeholders `{id}`, `{name}`, `{port}`, `{workingDirectory}` — a value with spaces is quoted for you, so it stays one argument (`${NAME}` is left alone for your shell). The start command can be a name on `PATH`, an absolute path, or a path relative to the working directory (`./run.sh`).

| Preset | Start command | Default port | Notes |
| --- | --- | --- | --- |
| **Azure Function** | `func start --port {port}` | `7071` | |
| **React / Vite** | `npm run dev -- --host 0.0.0.0 --port {port}` | `5173` | |
| **React (CRA)** | `npm start` | `3000` | sets `PORT={port}` env var |
| **Node API** | `npm run dev` | `3001` | health check `…/health` |
| **.NET API** | `dotnet run --urls http://localhost:{port}` | `5080` | health check `…/health` |
| **Docker Compose** | `docker compose up` | — | stop command `docker compose stop` |
| **Custom** | *(you provide)* | — | bring your own binary |

Unknown placeholders are left intact with a UI warning rather than silently emptied.

---

## 🔀 Reverse proxy

Three routes give you a single-origin stack:

| Match path | Destination | Transform | Resulting request |
| --- | --- | --- | --- |
| `/app/{**catch-all}` | `http://localhost:5173/` | `RemovePrefix /app` | `http://localhost:5050/app/dashboard` |
| `/api/{**catch-all}` | `http://localhost:5080/` | `None` | `http://localhost:5050/api/weather` |
| `/functions/{**catch-all}` | `http://localhost:7071/` | `RemoveAndAddPrefix /functions → /api` | `http://localhost:5050/functions/ping` |

Your browser sees one origin — `http://localhost:5050` — so CORS gets out of the way. Edits hot-reload into the live YARP snapshot without restarting DevDeck. The gateway adds no request-size limit of its own, so large uploads behave as they do against the service directly.

Each forwarded request is logged straight into the target service's log stream as a `[PRX]` pair — an inbound line and an outbound line carrying the response status, latency, and size:

```text
2026-05-25T09:14:02 [PRX] 127.0.0.1 --> GET /api/Catalog/items?page=2
2026-05-25T09:14:02 [PRX] 127.0.0.1 <-- 200 GET /api/Catalog/items -> http://localhost:7071/ 18ms 4.2 KB
```

> ⚠️ A catch-all route — any match path with no literal segment, such as `/`, `/{**catch-all}` or `/{**path}` — for a SPA fallback is **disabled by default**. Enable `DevDeck:ReverseProxy:AllowCatchAllRoutes` to use one — otherwise the route is persisted but skipped (with a warning) when the proxy config is built.

The route editor (and the importer) refuse a route that could only fail: Match hosts that aren't `host` / `host:port` (no scheme, no brackets), unknown route constraints (`{id:integer}`), a destination that is the gateway itself, an authorization policy other than blank/`Anonymous` (DevDeck has no authentication), or a route that matches exactly the same requests as another one with the same **Order** — give one of them a lower Order to make it win. Match hosts other than `localhost` / `*.localhost` must also be listed in [`AllowedHosts`](#-safety-model).

---

## 🔁 Import / Export

DevDeck can serialize your configuration to portable JSON and re-import it on another machine. Services, launch profiles, and proxy routes each export to their own bundle (`schemaVersion: 1`), with foreign keys referenced **by name** so documents survive moving between machines.

- **Export** from the toolbar on the Services, Profiles, and Proxy Routes pages — one entity or the whole set.
- **Import** merges by name: existing entries are updated, new ones created.
- **Secrets are masked by default** — exported secret env vars carry a placeholder unless you explicitly opt to include real values; on import, the placeholder means "keep what's already in the DB."

A sample route bundle ships in this repo as [`devdeck-main-react-api-routes.json`](devdeck-main-react-api-routes.json):

```jsonc
{
  "schemaVersion": 1,
  "routes": [
    {
      "name": "Catalog",
      "serviceName": "FunctionAppCatalog",
      "destinationUrlOverride": "http://localhost:7071/",
      "matchPath": "/api/Catalog/{**catch-all}",
      "order": 0,
      "pathTransformMode": "RemoveAndAddPrefix",
      "pathPrefixToRemove": "/api/Catalog",
      "pathPrefixToAdd": "/api"
    }
    // …more routes
  ]
}
```

---

## 🗂️ How it's organized

```text
DevDeck.Web/
  Areas/Manage/         MVC controllers + views for the dashboard
  Data/                 EF Core entities, DbContext, paths helper
  Services/
    Runtime/            DevDeckProcessManager + log ring buffer
    Logs/               LogFileWriter (durable {slug}-{run}-{stamp}.log files)
    Health/             HealthCheckBackgroundService + PortProbeService
    Proxy/              DevDeckProxyConfigProvider + ProxyRouteBuilder + ReservedPaths
    Commands/           Presets, executable resolver, template renderer
    Portability/        JSON import/export of services, profiles, routes
  wwwroot/              devdeck.css design system, brand/favicon assets, dashboard/logs JS
  Migrations/           EF Core SQLite migrations
DevDeck.Tests/          xUnit unit tests
```

**Storage layout** — the separation is deliberate and load-bearing:

| What | Where it lives |
| --- | --- |
| Configuration + run summaries | **SQLite** — `{LocalAppData}/DevDeck/devdeck.db` |
| Live process state, log ring buffer, YARP snapshot | **Memory** |
| Durable stdout/stderr | **Files** — `{LocalAppData}/DevDeck/logs/*.log` |

> Full log streams are never stored in SQLite, and the proxy never queries SQLite per request — it serves from an in-memory snapshot with a change token.

---

## ⚙️ Configuration

Settings live in `DevDeck.Web/appsettings.json` under the `DevDeck` section:

```json
{
  "DevDeck": {
    "DevelopmentOnly": true,
    "AutoStartEnabledServices": false,
    "StopServicesOnShutdown": true,
    "StopTimeoutSeconds": 10,
    "DashboardPollingMilliseconds": 1500,
    "MaxLiveLogLinesPerService": 5000,
    "LogTrimAmount": 1000,
    "LogRetentionDays": 14,
    "ReverseProxy": {
      "Enabled": true,
      "GatewayBaseUrl": "http://localhost:5050",
      "AllowExternalDestinations": false,
      "AllowCatchAllRoutes": false,
      "EnableAutoStartOnRequest": false,
      "LogProxyRequests": true
    }
  }
}
```

| Key | Default | What it does |
| --- | --- | --- |
| `DevelopmentOnly` | `true` | Gate management/execution actions behind a Development environment. |
| `AutoStartEnabledServices` | `false` | Start enabled services automatically once DevDeck is listening. |
| `StopServicesOnShutdown` | `true` | Stop every managed service (and a DevDeck-launched Azurite) when DevDeck shuts down. Turn it off to leave them running and have them re-attached on the next start — but a service's console output went through DevDeck, so one that keeps writing (Vite, CRA and other Node dev servers) exits on its next output line once DevDeck is gone; .NET hosts, `func` and quiet processes keep running. |
| `StopTimeoutSeconds` | `10` | How long a service's stop command may run, and then how long the process tree gets to exit after the graceful signal before it is force-killed. |
| `DashboardPollingMilliseconds` | `1500` | Dashboard status poll interval. |
| `MaxLiveLogLinesPerService` / `LogTrimAmount` | `5000` / `1000` | Ring-buffer size and trim step. |
| `LogRetentionDays` | `14` | Age after which on-disk log files are pruned (a still-running service's log is kept). |
| `ReverseProxy.GatewayBaseUrl` | `http://localhost:5050` | The single origin DevDeck (and the gateway) bind to. |
| `ReverseProxy.AllowExternalDestinations` | `false` | Permit proxy destinations outside localhost/private networks. |
| `ReverseProxy.AllowCatchAllRoutes` | `false` | Permit catch-all SPA-fallback routes (`/`, `/{**catch-all}`, or any match path with no literal segment). Reserved paths such as `/Manage` are never proxied either way. |
| `ReverseProxy.EnableAutoStartOnRequest` | `false` | (Reserved for future) start a service when its route is first hit. |
| `ReverseProxy.LogProxyRequests` | `true` | Log each proxied request as a `PRX` line pair — inbound request + outbound response (status, latency, size) — in the target service's log stream. |

`AllowExternalDestinations` is off by default — routes are restricted to `localhost`, `127.0.0.1`, `::1`, `*.localhost`, and RFC 1918 private networks (`10/8`, `172.16/12`, `192.168/16`). Flip it on only if you genuinely need to proxy something external.

`AllowedHosts` (top level, outside the `DevDeck` section) lists the host names DevDeck answers to — by default `localhost;*.localhost;127.0.0.1;[::1]`; the loopback names and a concrete `GatewayBaseUrl` host are always added. Requests for any other host get HTTP 400. If a proxy route matches on another host name (say `api.mydev.test`), add it here.

---

## 🔒 Safety model

DevDeck spawns arbitrary local processes and exposes a reverse proxy, so a few rules are non-negotiable:

- **No raw command endpoint.** Every process start comes from a stored, validated service definition — there is no `POST /api/run` that takes a command string.
- **`/Manage` is reserved.** Proxy routes cannot match `/Manage`, `/manage`, `/css`, `/js`, `/lib`, `/images`, `/favicon.ico`, or `/_devdeck`.
- **MVC routes are mapped before YARP** — DevDeck's own UI always wins over a misconfigured proxy.
- **Catch-all routes are disabled** unless `AllowCatchAllRoutes` is explicitly set.
- **Destinations default to localhost / private networks**; public hosts require `AllowExternalDestinations`.
- **Secrets** (env vars marked `IsSecret`) are masked in the UI and never logged.
- **Only its own host names.** DevDeck has no authentication, so it rejects requests whose `Host` isn't in `AllowedHosts` (loopback by default). That stops a malicious web page from using DNS rebinding — making its own host name resolve to `127.0.0.1` — to drive DevDeck from your browser. Don't set `AllowedHosts` to `*`.

DevDeck is designed for **local development only** — it isn't a production process manager or ingress gateway.

---

## 🛠️ Development

```sh
dotnet build                                            # build solution
dotnet test                                             # run all unit tests
dotnet run --project DevDeck.Web                        # launch on http://localhost:5050
dotnet ef migrations add <Name> --project DevDeck.Web -o Migrations
```

The `DevDeck.Tests` project covers the cross-platform-sensitive bits: command template rendering and argument quoting, executable resolution for both OSes, log buffer trim behavior, reverse-proxy transform building and route validation (including a real Kestrel + YARP host), reserved-path rejection, destination validation, import/export round-trips, and real process trees being stopped and re-attached (POSIX tests, plus a Windows Ctrl+C test) — run it on each OS you change runtime code for.

> 🤖 Working with an AI assistant? [`CLAUDE.md`](CLAUDE.md) (and the mirrored [`AGENTS.md`](AGENTS.md)) capture the architecture, milestone order, and safety constraints agents should follow.

---

## 🗺️ Roadmap

**Implemented (v1)**

- ✅ Service CRUD, presets, environment vars, health checks
- ✅ Process supervisor with run history + log files
- ✅ Launch profiles with ordered start
- ✅ YARP reverse proxy with hot reload, transforms, and validation
- ✅ Import / export of services, profiles, and routes
- ✅ Dashboard polling, custom dark UI

**Future work**

- ⏳ SignalR live log streaming (currently HTTP polling)
- ⏳ Auto-start-on-proxy-request (currently returns 503 when destination is down)
- ⏳ Host-based proxy routing (`app.localhost:5050`)
- ⏳ WSL execution mode
- ⏳ HTTPS / TLS for the gateway
- ⏳ DPAPI-backed secret encryption

---

## 📄 License

MIT — see [LICENSE](LICENSE).
