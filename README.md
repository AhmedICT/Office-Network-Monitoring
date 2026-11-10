# CBH-NMS — Centralized Branch-to-Head Office Network Monitoring

PDCL's in-house network monitoring system for 35 branches / ~300 devices. Windows-native, PostgreSQL 17 + TimescaleDB.

Requirements: [CBH-NMS_SRS.md](CBH-NMS_SRS.md) · UI prototype: [CBH-NMS_UI_Design.html](CBH-NMS_UI_Design.html) ·
Deviations from the SRS: [docs/DECISIONS.md](docs/DECISIONS.md) · Production install: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)

## Layout

| Path | What | Runs on |
| --- | --- | --- |
| `src/Cbh.Nms.Core` | ASP.NET Core 10: `/agent/v1`, `/api/v1`, SignalR `/hubs/status`, evaluation engine, notifications, reports; serves the UI | HO app server (IIS or Windows Service) |
| `src/Cbh.Nms.Web` | React + TypeScript UI (SCR-01…09), built into Core's `wwwroot` | browser |
| `src/Cbh.Nms.Agent` | Branch Agent Windows Service: ICMP, SNMPv3, HTTP/TCP checks, SQLite store-and-forward | one always-on PC per branch |
| `src/Cbh.Nms.Poller` | Central Poller Windows Service: pings each branch over MPLS and VPN every 30 s | HO app server |
| `src/Cbh.Nms.Shared` | Agent ↔ HO wire contracts, constants | — |
| `db/migrations` | Schema (applied automatically by Core at startup) | HO DB server |
| `deploy/` | `publish.ps1`, `install-ho.ps1`, `install-agent.ps1` | — |
| `tests/Cbh.Nms.Tests` | xUnit tests (100) | — |

## Run it on this PC

Needs Docker Desktop (database only), .NET 10 SDK and Node 20+.

```powershell
.\run-local.ps1                       # DB container, UI build, Core, Poller; opens http://localhost:5219
# In the UI: Branches & Devices → add a branch → Agents → "+ Enrollment token", then:
.\run-local.ps1 -SkipUiBuild -EnrollToken <token>   # also starts a Branch Agent
.\run-local.ps1 -Stop                 # stop Core / Poller / Agent
```

Your Windows account becomes the first Super Admin. In development, e-mails are written as `.eml` files to
`C:\ProgramData\CBH-NMS\dev-mail` instead of being sent; SMS is skipped until a gateway is configured.

## Build and test

```powershell
dotnet build; dotnet test                              # backend
cd src\Cbh.Nms.Web; npm install; npm run build         # UI → src\Cbh.Nms.Core\wwwroot
.\deploy\publish.ps1                                   # everything → .\publish
```

## Status against the SRS

All **M** (MVP) requirements are implemented. Verified end-to-end on a dev PC: agent registration, metrics
ingestion, branch status (Online/Degraded/Unknown), HIS check failure → Critical alert → incident after 3 min,
e-mail notification, ack/assign/resolve/close, availability report with PDF/Excel export, store-and-forward
across a Core restart with no data gap.

| Module | State |
| --- | --- |
| Inventory: branches, ISP links, contacts, devices, dependencies, agents, SNMP profiles, HIS checks, CSV/XLSX import (FR-BR-01…06) | Done |
| Availability: ICMP, ISP links, branch status §9.1, monitoring-lost, monthly availability (FR-AV-01…06) | Done |
| HO ↔ branch over MPLS and VPN: both paths polled, one down = Degraded + alert, both down = Offline (D-13) | Done |
| Devices: router/firewall, server, UPS via SNMPv3 profiles (FR-DV-01/03/04) | Done |
| HIS/LIS HTTP + TCP checks (FR-AP-01/02) | Done |
| Alerts: rules, severities, dependency suppression, flapping, maintenance, channels, recipients, ack (FR-AL-01…08) | Done |
| Incidents: auto-create, states, root cause, escalation, MTTD/MTTA/MTTR, Task Management sync (FR-IN-01…06) | Done; Task Management API contract assumed — see D-09 |
| Dashboard: KPIs, tiles, live updates, NOC mode (FR-DB-01/02/04/05) | Done |
| Reports: availability, incidents, ISP, power; PDF/Excel; monthly e-mail (FR-RP-01…05) | Done |
| Users: AD login (Windows + LDAPS), group→role, branch scope, append-only audit (FR-US-01…04) | Done |
| Not yet built (Phase 2/3): discovery (BR-07), switch ports (DV-02), Wi-Fi clients (DV-05), syslog/traps (DV-06), bandwidth (BW-01…04), LIS last-result age (AP-03), repeat-failure flag (IN-07), map (DB-03), agent auto-update, signed MSI | Pending |
