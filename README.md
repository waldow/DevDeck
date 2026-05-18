# DevDeck

**Start your local stack from one place.**

DevDeck is a local-developer dashboard that supervises every process your project needs — Azure Functions, Vite frontends, .NET APIs, Node services, Docker Compose, anything — and exposes them all behind one reverse-proxy origin. Configure once, then start, stop, restart, monitor logs, and check health from a single browser tab.

Built on ASP.NET Core MVC (.NET 10), SQLite, and YARP. Runs cross-platform on Windows and Linux/WSL.

---

## Why

Local development usually means juggling six terminals:

```
Terminal 1: cd backend && func start --port 7071
Terminal 2: cd frontend && npm run dev -- --port 5173
Terminal 3: cd api && dotnet run --urls http://localhost:5080
Terminal 4: docker compose up
Terminal 5: tail -f logs/...
Terminal 6: trying to remember which one to Ctrl+C
```

DevDeck collapses that into one UI. Logs stream into a single panel. Health pills tell you what's actually up. A built-in reverse proxy at `http://localhost:5050` makes your frontend, API, and functions share an origin — so CORS stops being a daily nuisance.

---

## Features

**Process supervision**
- Start, stop, restart, and watch any local command (`npm run dev`, `func start`, `dotnet run`, `docker compose up`, custom binaries).
- Per-service environment variables, with secret masking in the UI.
- Process-tree kill on stop, so `npm` and its children all go down together.
- Run history per service with start/stop timestamps, exit codes, and downloadable log files.

**Launch profiles**
- Group services into named profiles ("Full Stack Dev", "Frontend Only").
- Ordered start with per-service start delays.

**Live + persistent logs**
- 5,000-line in-memory ring buffer per service, displayed in a monospace panel that auto-scrolls.
- Every line also written to `{service-slug}-{run-id}-{timestamp}.log` for archival.
- `[OUT]`, `[ERR]`, `[SYS]` stream tagging.

**Health & port awareness**
- HTTP health checks run on a background interval, with `Healthy`/`Unhealthy`/`Timeout`/`NotRunning` states.
- Port probes warn you when a configured port is already in use.

**Reverse proxy (YARP) — first class**
- Path-based routing: `/app → :5173`, `/api → :5080`, `/functions → :7071/api`.
- Path transforms: `None`, `RemovePrefix`, `AddPrefix`, `RemoveAndAddPrefix`, `SetPath`.
- Reserved paths (`/Manage`, `/css`, `/js`, …) cannot be hijacked by routes.
- Destinations default to localhost / 127.0.0.1 / `*.localhost` / private CIDRs; public hosts require an explicit opt-in.
- Hot-reload on every route edit — no DevDeck restart needed.

**Cross-platform**
- `npm` → `npm.cmd` on Windows, `npm` on Linux/macOS — automatic.
- SQLite + log files under `%LOCALAPPDATA%\DevDeck` (Windows) or `~/.local/share/DevDeck` (Linux/WSL).

---

## Quick start

Requires the **.NET 10 SDK**.

```sh
git clone <your-fork-or-repo-url>
cd DevDeck
dotnet run --project DevDeck.Web
```

Then open **http://localhost:5050**.

DevDeck migrates its SQLite database on first launch and starts with an empty dashboard. Click **+ New service**, pick a preset (Azure Function, React/Vite, Node API, .NET API, Docker Compose, or Custom), point it at a working directory, and hit **Start**.

---

## Reverse-proxy example

Three routes give you a single-origin stack:

| Match path                | Destination                | Transform                       | Resulting URL                              |
| ------------------------- | -------------------------- | ------------------------------- | ------------------------------------------ |
| `/app/{**catch-all}`      | `http://localhost:5173/`   | `RemovePrefix /app`             | `http://localhost:5050/app/dashboard`      |
| `/api/{**catch-all}`      | `http://localhost:5080/`   | `None`                          | `http://localhost:5050/api/weather`        |
| `/functions/{**catch-all}`| `http://localhost:7071/`   | `RemoveAndAddPrefix /functions → /api` | `http://localhost:5050/functions/ping` |

Your browser sees one origin — `http://localhost:5050` — so CORS gets out of the way and cookies behave consistently across the frontend and backend.

---

## How it's organized

```
DevDeck.Web/
  Areas/Manage/         MVC controllers + views for the dashboard
  Data/                 EF Core entities, DbContext, paths helper
  Services/
    Runtime/            DevDeckProcessManager + log ring buffer
    Logs/               LogFileWriter (durable {slug}-{run}-{stamp}.log files)
    Health/             HealthCheckBackgroundService + PortProbeService
    Proxy/              DevDeckProxyConfigProvider + ProxyRouteBuilder + ReservedPaths
    Commands/           Presets, executable resolver, template renderer
  wwwroot/              devdeck.css design system + dashboard/logs JS
  Migrations/           EF Core SQLite migrations
DevDeck.Tests/          xUnit unit tests
```

**Storage layout:**

| What                | Where it lives                                      |
| ------------------- | --------------------------------------------------- |
| Configuration + run summaries | SQLite — `{LocalAppData}/DevDeck/devdeck.db` |
| Live process state, log ring buffer, YARP snapshot | Memory                       |
| Durable stdout/stderr | Files — `{LocalAppData}/DevDeck/logs/*.log`        |

---

## Configuration

Settings live in `DevDeck.Web/appsettings.json` under the `DevDeck` section:

```json
{
  "DevDeck": {
    "DevelopmentOnly": true,
    "AutoStartEnabledServices": false,
    "StopTimeoutSeconds": 10,
    "DashboardPollingMilliseconds": 1500,
    "MaxLiveLogLinesPerService": 5000,
    "LogTrimAmount": 1000,
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

`AllowExternalDestinations` is off by default — proxy routes are restricted to `localhost`, `127.0.0.1`, `::1`, `*.localhost`, and RFC 1918 private networks. Flip it on if you genuinely need to proxy something external.

---

## Safety model

DevDeck spawns arbitrary local processes, so a few rules are non-negotiable:

- **No raw command endpoint.** Every process start comes from a stored, validated service definition.
- **`/Manage` is reserved.** Proxy routes cannot match `/Manage`, `/manage`, `/css`, `/js`, `/lib`, `/images`, `/favicon.ico`, or `/_devdeck`.
- **MVC routes are mapped before YARP** — DevDeck's own UI always wins over a misconfigured proxy.
- **Catch-all routes (`/`, `/{**catch-all}`) are disabled** unless explicitly overridden.
- **Secrets** (env vars marked `IsSecret`) are masked in the UI.

DevDeck is designed for local development only — it isn't a production process manager or ingress gateway.

---

## Development

```sh
dotnet build                                            # build solution
dotnet test                                             # run all unit tests
dotnet run --project DevDeck.Web                        # launch on http://localhost:5050
dotnet ef migrations add <Name> --project DevDeck.Web -o Migrations
```

The `DevDeck.Tests` project covers the cross-platform-sensitive bits: command template rendering, executable resolution for both OSes, log buffer trim behavior, reverse-proxy transform building, reserved-path rejection, and destination validation.

---

## Roadmap

Currently implemented (v1):
- Service CRUD, presets, environment vars, health checks
- Process supervisor with run history + log files
- Launch profiles with ordered start
- YARP reverse proxy with hot reload, transforms, and validation
- Dashboard polling, custom dark UI

Future work (not yet implemented):
- SignalR live log streaming (currently HTTP polling)
- Auto-start-on-proxy-request (currently returns 503 when destination is down)
- Host-based proxy routing (`app.localhost:5050`)
- WSL execution mode
- HTTPS / TLS for the gateway
- DPAPI-backed secret encryption

---

## License

MIT — see [LICENSE](LICENSE).
