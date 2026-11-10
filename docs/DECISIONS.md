# Implementation decisions and deviations from SRS v0.2

Each item below is a place where the code differs from, or settles a question left open by, `CBH-NMS_SRS.md`.
They should be folded back into the SRS at its next revision.

## D-01 Offline detection threshold is 90 s, not 3 min (resolves NFR-01 vs §9.1)

§9.1 marks a branch Offline only after 3 minutes without a heartbeat, but NFR-01 requires an offline
branch on the dashboard within 2 minutes. `BranchStatusRules` now uses two thresholds:

| Condition | Threshold | Result |
| --- | --- | --- |
| Poller ping failing **and** heartbeat missing | 90 s (3 missed 30 s cycles) | Offline-Power / Offline-ISP |
| Heartbeat missing, poller ping OK | 3 min (FR-AV-05 unchanged) | Unknown |

Worst case to dashboard: 90 s + 15 s evaluation tick + SignalR ≈ 1 min 50 s.

## D-02 Central Poller results have their own table

`mon.metric_raw` requires `agent_id`/`agent_seq`, which the Central Poller does not have.
Poller pings go to a separate hypertable `mon.reachability (time, branch_id, success, rtt_ms)`.

## D-03 Schema fixes

- `org.region`, `inv.device_type`, `inv.snmp_profile`, `sec.app_user` are defined before use.
- `ops.alert.incident_id` removed; the alert↔incident link is only `ops.incident_alert` (one source of truth).
- `cbhnms_app` role is created by the migration; `sec.audit_log` gets only SELECT/INSERT (FR-US-04).
- Added: `org.branch.sla_target_pct`, per-branch `loss_threshold_pct` / `latency_threshold_ms` (§9.1 says
  they are per-branch), `org.isp_link.probe_target`, `inv.enroll_token`, `inv.branch_config_version`,
  `ops.incident.started_at` (needed for MTTD), `sec.user_branch_scope.scope_id`.
- `ops.incident` has a CHECK that Resolved/Closed incidents carry a root cause (FR-IN-04).

## D-04 Role permissions (§7.2 vs §9.4 disagree)

The §9.4 matrix is treated as authoritative. Alert-rule editing will be Super Admin only (SCR-09);
reports will include Net Admin. Implemented so far: Viewer, EditInventory (Super Admin + Net Admin), Ack, Admin.

## D-05 Agent events

State changes (`up`, `ups_on_battery`) are sent through `/agent/v1/metrics` flagged in the local buffer
as events: they trigger an immediate send and are never pruned. `/agent/v1/events` is deferred to
Phase 2 together with SNMP traps/syslog, which are its only other producers.

## D-06 Schema ownership

The schema is owned by plain SQL files in `db/migrations`, applied in order by `SqlMigrator` at Core
startup (recorded in `public.schema_migrations`). EF Core maps onto it but never generates migrations,
because hypertables, compression/retention policies and continuous aggregates are not expressible in EF.

## D-07 Background jobs without Hangfire

SRS §3.1 names Hangfire. The jobs (evaluation, incidents/escalation, notification outbox, monthly report)
are plain `BackgroundService`s with their state in PostgreSQL (`ops.notification_log` is the outbox,
`ops.incident.ticket_next_attempt_at` drives Task Management retries, `ops.system_flag` makes monthly jobs
run once). One less dependency and schema; behaviour (retries at 1/5/15 min) is the same.
Run a single Core instance; scaling out would need row locking on the outbox.

## D-08 PDF export pinned to PDFsharp-MigraDoc 6.1.1

6.2+ adds a runtime license check that fails without a paid key; 6.1.1 is MIT. QuestPDF was not used because
its free licence is limited to organisations under USD 1M revenue. Fonts come from `C:\Windows\Fonts`.

## D-09 Task Management API contract is assumed

The PDCL Task Management API is not documented yet (SRS §1.4). `TaskManagementClient` assumes:
`POST {BaseUrl}/api/tickets` → `{"id": "..."}`; `PATCH {BaseUrl}/api/tickets/{id}` with status/assignee;
callback `POST /api/v1/integrations/taskmgmt/callback` with header `X-Api-Key` and body
`{ticketId, status, assignee, rootCause, note}`. Paths are configurable; field names may need adjusting.

## D-10 Login flow

Browsers get a cookie session from either `GET /api/v1/auth/windows` (Negotiate — silent on domain PCs) or
`POST /api/v1/auth/login` (LDAPS bind, for phones and non-domain PCs; 5 failures → 15 min lock).
AD groups map to roles via `Auth:GroupRoleMap`; unmapped users must be added in Admin → Users.
One break-glass account (`Auth:BreakGlass`, PBKDF2 hash) works when AD is down.

## D-11 Notification audience

Level 1 (§9.2) = users with role Branch Engineer whose scope covers the branch + all Network Administrators
(the NOC). Extra recipients come from `ops.notification_rule` (Admin → Notifications). Unacknowledged
Critical/High alerts get a reminder SMS every 30 min (`Notifications:RepeatMinutes`), capped by
`SmsDailyLimit` and `SmsMonthlyBudgetBdt`.

## D-13 HO ↔ branch connectivity: MPLS and VPN

Branches reach HO over MPLS and over a site-to-site VPN (IPsec across the internet ISP links); either may be the
only path or the backup of the other. The SRS only mentions MPLS. Implemented:

- `org.branch.vpn_router_ip` next to `mpls_router_ip` (both optional; Branches & Devices → Edit branch).
- The Central Poller pings each configured IP every 30 s; `mon.reachability.path` is `MPLS` or `VPN`.
- §9.1 uses *all* configured paths: the branch is Offline only when every path has failed ≥ 90 s and the agent
  is silent. One path down while another answers → **Degraded** ("MPLS path to HO down — running on VPN").
- Each path down raises its own alert (`hopath:{branch}:{path}`; High for Tier-1, otherwise Warning), so the
  NOC can call the MPLS provider / check the VPN while the branch still works. When the whole branch is offline
  these alerts are suppressed as `PARENT_DOWN` (one notification, FR-AL-03).
- SCR-03 shows MPLS / VPN state, RTT and loss, and their latency in the 24 h chart (`GET /branches/{id}/ho-paths`).
- The agent needs no change: it uses one HO URL and Windows routing picks the working path.
  `Agent:Auth:AllowedNetworks` must include the source ranges of both paths.

## D-12 Development database runs in Docker

Production follows SRS §2.3 (PostgreSQL for Windows). On developer PCs `run-local.ps1` uses the
`timescale/timescaledb:latest-pg17` container because the Windows TimescaleDB installer is fiddly to
automate. The schema and code are identical.
