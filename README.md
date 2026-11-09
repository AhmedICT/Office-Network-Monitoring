# CBH-NMS — Centralized Branch-to-Head Office Network Monitoring

PDCL's in-house network monitoring system. Requirements: [CBH-NMS_SRS.md](CBH-NMS_SRS.md) ·
UI prototype: [CBH-NMS_UI_Design.html](CBH-NMS_UI_Design.html) · Deviations: [docs/DECISIONS.md](docs/DECISIONS.md)

## Layout

| Path | What | Runs on |
| --- | --- | --- |
| `src/Cbh.Nms.Core` | ASP.NET Core 10: `/agent/v1`, `/api/v1`, SignalR `/hubs/status`, status engine | HO app server (IIS or Windows Service) |
| `src/Cbh.Nms.Agent` | Branch Agent Windows Service: ICMP, SNMPv3, HTTP/TCP checks, SQLite buffer | One always-on PC per branch |
| `src/Cbh.Nms.Poller` | Central Poller Windows Service: pings branch MPLS routers every 30 s | HO app server |
| `src/Cbh.Nms.Shared` | Agent ↔ HO wire contracts, status/severity/role constants | — |
| `db/migrations` | PostgreSQL 17 + TimescaleDB schema, applied by Core at startup | HO DB server |
| `tests/Cbh.Nms.Tests` | xUnit tests | — |

## Build and test

```powershell
dotnet build
dotnet test
```

## Run locally

1. Install PostgreSQL 17 and the matching TimescaleDB Windows package; add
   `shared_preload_libraries = 'timescaledb'` to `postgresql.conf` and restart.
2. `createdb -U postgres cbhnms`
3. Set your Windows account as the first Super Admin, then start Core (it applies the schema):
   ```powershell
   $env:Bootstrap__SuperAdminUser = "$env:USERDOMAIN\$env:USERNAME"
   dotnet run --project src/Cbh.Nms.Core
   ```
4. Create a region and branch (SQL or `POST /api/v1/branches`), then
   `POST /api/v1/agents/enroll-token {"branchId":1}` to get a one-time token.
5. Start the agent with that token (it registers once and stores its key with DPAPI):
   ```powershell
   $env:Agent__HoBaseUrl = "https://localhost:7226/"
   $env:Agent__EnrollToken = "<token>"
   $env:Agent__DataDirectory = "$env:TEMP\cbhnms-agent"
   dotnet run --project src/Cbh.Nms.Agent
   ```
6. Optionally start the Central Poller: `dotnet run --project src/Cbh.Nms.Poller`.

## Status (MVP plan, SRS §10.1)

| Step | State |
| --- | --- |
| DB schema (26 tables + reachability + 2 continuous aggregates) | Written; not yet run against a live PostgreSQL |
| Agent API: register, config (ETag), metrics (COPY + dedup), heartbeat | Done |
| Branch Agent: ICMP, SNMPv3, HTTP/TCP, store-and-forward, DPAPI key | Done; MSI installer pending |
| Central Poller | Done |
| Branch status engine (§9.1) + SignalR push | Done |
| Branch/device/agent CRUD, scope filtering, audit | Done (bulk import FR-BR-06 pending) |
| Alert rules, suppression, flapping, notifications, escalation | Next |
| Incidents + Task Management integration | Pending |
| React dashboard (SCR-01..09) | Pending |
| Reports (PDF/Excel) | Pending |
