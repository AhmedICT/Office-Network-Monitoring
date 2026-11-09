# Production deployment (SRS §2.3, §10.1)

## 1. Database server (Windows Server, separate VM)

1. Install PostgreSQL 17 (EDB installer) and the matching TimescaleDB Windows package; pin both versions (§6.4).
2. `postgresql.conf`: `shared_preload_libraries = 'timescaledb'`, `ssl = on` (certificate from the PDCL CA). Restart.
3. Create the database and login (the schema itself is created by Core on first start):
   ```sql
   CREATE DATABASE cbhnms;
   CREATE ROLE cbhnms LOGIN PASSWORD '…';
   ALTER DATABASE cbhnms OWNER TO cbhnms;
   \c cbhnms
   CREATE EXTENSION timescaledb;
   ```
4. Backups (NFR-12): pgBackRest or `pg_basebackup` nightly + WAL archiving; test a restore monthly.

## 2. HO app server

```powershell
# on a build PC
.\deploy\publish.ps1
# copy .\publish and .\deploy to the server, then (elevated):
.\deploy\install-ho.ps1 -ConnectionString "Host=db01;Database=cbhnms;Username=cbhnms;Password=…;SSL Mode=Require" `
                        -SuperAdmin "PDCL\your.account" -Urls "https://+:443"
```

Then edit `C:\Program Files\CBH-NMS\Core\appsettings.Production.json` and restart the `CBH-NMS Core` service:

```json
{
  "Kestrel": { "Endpoints": { "Main": { "Url": "https://+:443",
    "Certificate": { "Subject": "nms.pdcl.local", "Store": "My", "Location": "LocalMachine" } } } },
  "Agent": { "HoProbeTarget": "10.0.0.10", "Auth": { "AllowedNetworks": [ "10.0.0.0/8" ] } },
  "Auth": {
    "GroupRoleMap": { "PDCL\NMS-SuperAdmins": "SUPER_ADMIN", "PDCL\NMS-HeadOfIT": "HEAD_OF_IT",
                      "PDCL\NMS-ITManagers": "IT_MANAGER", "PDCL\NMS-NOC": "NET_ADMIN",
                      "PDCL\NMS-BranchEngineers": "BRANCH_ENGINEER", "PDCL\NMS-Management": "MANAGEMENT" },
    "Ldap": { "Server": "dc01.pdcl.local", "Domain": "PDCL", "BaseDn": "DC=pdcl,DC=local" },
    "BreakGlass": { "User": "nms-breakglass", "PasswordHash": "<see below>" }
  },
  "Notifications": { "PublicBaseUrl": "https://nms.pdcl.local/", "SmsMonthlyBudgetBdt": 2000 },
  "Email": { "Host": "mail.pdcl.local", "Port": 587, "User": "cbh-nms", "Password": "…", "From": "cbh-nms@pdcl.local" },
  "Sms": { "Url": "https://sms.vendor.com.bd/api/send", "Method": "POST", "ContentType": "application/json",
           "BodyTemplate": "{\"api_key\":\"{apiKey}\",\"to\":\"{to}\",\"msg\":\"{message}\"}",
           "ApiKey": "…", "SuccessContains": "success", "CostPerSmsBdt": 0.35 },
  "TaskManagement": { "BaseUrl": "https://tasks.pdcl.local/", "ApiKey": "…", "CallbackApiKey": "…",
                      "CallbackAllowedIps": [ "10.0.0.25" ] }
}
```

- Break-glass hash: in a C# REPL or test, `BreakGlassOptions.Hash("long-random-password")`; store the password in the safe.
- Data-protection keys (encrypt SNMP secrets) live in `C:\ProgramData\CBH-NMS\keys` — include that folder in backups.
- Health check for your own monitoring: `GET /health` (200 = app, DB, Central Poller and engine all fresh).

**IIS instead of a service:** create a site with an *No Managed Code* app pool pointing at `publish\Core`
(the publish includes `web.config`), enable Windows Authentication + Anonymous on the site, and run the Poller
as a service with `install-ho.ps1` (or `New-Service`). Set the app pool to *AlwaysRunning* and disable idle
time-out — the evaluation engine runs inside Core.

## 3. Each branch

1. SNMPv3 read-only user (authPriv SHA/AES) on FortiGate / Cisco / MikroTik and UPS cards; no v1/v2c (§2.4).
2. In the UI: add the branch (MPLS router IP), ISP links (probe IPs), devices (or bulk import), SNMP profile.
3. Agents tab → **+ Enrollment token** (valid 24 h, single use).
4. On the always-on branch PC (elevated), from `publish\Agent`:
   ```powershell
   .\install-agent.ps1 -HoBaseUrl "https://nms.pdcl.local/" -EnrollToken "<token>"
   ```
   The agent needs only outbound HTTPS to HO; open no inbound ports.

## 4. Pilot acceptance (§10.2)

Pull the ISP cable, switch off UPS mains, stop the HIS service, and confirm each appears on the dashboard
within 2 minutes with the right status, SMS within 3 minutes, a single notification when the router is down,
and back-filled graphs after an hour offline.
