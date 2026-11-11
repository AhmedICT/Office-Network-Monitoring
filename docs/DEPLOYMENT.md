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
  "Agent": { "HoProbeTarget": "10.0.0.10", "Auth": { "AllowedNetworks": [ "10.0.0.0/8", "172.16.0.0/12" ] } },
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
2. In the UI: add the branch with its **MPLS router IP** and/or **VPN tunnel IP** (the branch end of the
   site-to-site VPN), ISP links (probe IPs), devices (or bulk import), SNMP profile.
   The Central Poller pings both IPs from HO every 30 s, so allow ICMP echo from the HO app server to each of
   them (MPLS router ACL and the branch firewall's VPN policy). `Agent:Auth:AllowedNetworks` must cover the
   source addresses agents use over **both** paths (MPLS range and the VPN/branch LAN range), otherwise the
   agent is refused while the branch is on VPN.
3. Agents tab → **+ Enrollment token** (valid 24 h, single use).
4. On the always-on branch PC (elevated), from `publish\Agent`:
   ```powershell
   .\install-agent.ps1 -HoBaseUrl "https://nms.pdcl.local/" -EnrollToken "<token>"
   ```
   The agent needs only outbound HTTPS to HO; open no inbound ports.

## 4. User PCs (Endpoint Agent, D-14)

1. In the UI: **User PCs → Enrollment keys → + Enrollment key** for the branch (reusable, e.g. 30 days). Copy the key.
2. On each PC (elevated), from `publish\Endpoint` — or push the folder and command with GPO / SCCM / Intune:
   ```powershell
   .\install-endpoint.ps1 -HoBaseUrl "https://nms.pdcl.local/" -EnrollKey "<key>"
   ```
   Installs the service *CBH-NMS Endpoint Agent* and the logon task *CBH-NMS Session Helper*. Outbound HTTPS only.
   Remove with `.\install-endpoint.ps1 -Uninstall`.
3. The PC appears under **User PCs** within a minute; full inventory follows within ~30 s.
4. Optional: **User PCs → Software rules** (e.g. BLOCKED `*torrent*`, REQUIRED your antivirus).
5. Application usage stays **off** until a Super Admin turns it on under **User PCs → Activity tracking**, after PDCL's
   written policy is in place and staff are informed. See D-14 for exactly what is and is not collected.

## 5. Pilot acceptance (§10.2)

Pull the ISP cable, switch off UPS mains, stop the HIS service, and confirm each appears on the dashboard
within 2 minutes with the right status, SMS within 3 minutes, a single notification when the router is down,
and back-filled graphs after an hour offline.
