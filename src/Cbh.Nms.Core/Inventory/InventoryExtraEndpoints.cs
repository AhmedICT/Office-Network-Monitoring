using System.Globalization;
using System.Net;
using System.Text.Json;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using ClosedXML.Excel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Inventory;

public sealed record RegionInput(string Name);
public sealed record IspInput(string Name, string? SupportPhone, string? SupportEmail);
public sealed record IspLinkInput(int IspId, string LinkRole, string? CircuitId, int BandwidthMbps, string? WanInterface,
    string? ProbeTarget, bool IsActive = true);
public sealed record ContactInput(string Name, string? Role, string? Phone, string? Email, bool IsPrimary);
public sealed record AppCheckInput(int? BranchId, string Name, string CheckType, string Target, short? ExpectedStatus,
    int? TimeoutMs, string? BusinessService, bool IsActive = true);
public sealed record SnmpProfileInput(string Name, string? Vendor, string SnmpUser, string AuthProtocol, string PrivProtocol,
    string? AuthSecret, string? PrivSecret, Dictionary<string, string>? Oids);

/// <summary>The rest of SCR-04: regions, ISPs and links, contacts, HIS checks, SNMP profiles, bulk import (FR-BR-02/06).</summary>
public static class InventoryExtraEndpoints
{
    public static void MapInventoryExtraApi(this IEndpointRouteBuilder api)
    {
        var view = api.MapGroup("").RequireAuthorization(AuthPolicies.Viewer);
        var edit = api.MapGroup("").RequireAuthorization(AuthPolicies.EditInventory);

        edit.MapPost("/regions", CreateRegion);
        view.MapGet("/device-types", (NmsDb db) => db.DeviceTypes.AsNoTracking().OrderBy(t => t.DeviceTypeId).ToListAsync());

        view.MapGet("/isps", (NmsDb db) => db.Isps.AsNoTracking().OrderBy(i => i.Name).ToListAsync());
        edit.MapPost("/isps", CreateIsp);
        edit.MapPut("/isps/{id:int}", UpdateIsp);

        edit.MapPost("/branches/{branchId:int}/isp-links", (int branchId, IspLinkInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveIspLink(branchId, null, i, h, db, ct));
        edit.MapPut("/branches/{branchId:int}/isp-links/{id:int}", (int branchId, int id, IspLinkInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveIspLink(branchId, id, i, h, db, ct));

        edit.MapPut("/branches/{branchId:int}/contacts", ReplaceContacts);

        view.MapGet("/app-checks", (int? branchId, NmsDb db) => db.AppChecks.AsNoTracking()
            .Where(c => branchId == null || c.BranchId == branchId || c.BranchId == null).OrderBy(c => c.Name).ToListAsync());
        edit.MapPost("/app-checks", (AppCheckInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveAppCheck(null, i, h, db, ct));
        edit.MapPut("/app-checks/{id:int}", (int id, AppCheckInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveAppCheck(id, i, h, db, ct));

        edit.MapGet("/snmp-profiles", (NmsDb db) => db.SnmpProfiles.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new { p.SnmpProfileId, p.Name, p.Vendor, p.SnmpUser, p.AuthProtocol, p.PrivProtocol, p.Oids }).ToListAsync());
        edit.MapPost("/snmp-profiles", (SnmpProfileInput i, HttpContext h, NmsDb db, IDataProtectionProvider dp, CancellationToken ct) => SaveSnmpProfile(null, i, h, db, dp, ct));
        edit.MapPut("/snmp-profiles/{id:int}", (int id, SnmpProfileInput i, HttpContext h, NmsDb db, IDataProtectionProvider dp, CancellationToken ct) => SaveSnmpProfile(id, i, h, db, dp, ct));

        edit.MapPost("/devices/import", Import).DisableAntiforgery();
        edit.MapGet("/devices/import/template", () => Results.File(System.Text.Encoding.UTF8.GetBytes(
            "branchCode,deviceType,vendor,hostName,ipAddress,model,serialNo,firmware,warrantyEnd,isCritical,parentHostName\r\n" +
            "SAV01,Firewall,FORTINET,SAV01-FGT-01,10.10.0.1,FortiGate 60F,FGT60F000001,7.2.8,2027-12-31,true,\r\n" +
            "SAV01,Switch,CISCO,SAV01-SW-01,10.10.0.2,CBS350-24,CBS000001,3.3,2027-12-31,false,SAV01-FGT-01\r\n"),
            "text/csv", "cbhnms-device-import-template.csv"));
    }

    private static async Task<IResult> CreateRegion(RegionInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return Invalid("name", "Required");
        if (await db.Regions.AnyAsync(r => r.Name == i.Name.Trim(), ct)) return Invalid("name", "Region already exists");
        var r = new Region { Name = i.Name.Trim() };
        db.Regions.Add(r);
        db.Record(h, "REGION_CREATE", "region", null, newValue: i);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/regions/{r.RegionId}", r);
    }

    private static async Task<IResult> CreateIsp(IspInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return Invalid("name", "Required");
        if (await db.Isps.AnyAsync(x => x.Name == i.Name.Trim(), ct)) return Invalid("name", "ISP already exists");
        var isp = new Isp { Name = i.Name.Trim(), SupportPhone = i.SupportPhone, SupportEmail = i.SupportEmail };
        db.Isps.Add(isp);
        db.Record(h, "ISP_CREATE", "isp", null, newValue: i);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/isps/{isp.IspId}", isp);
    }

    private static async Task<IResult> UpdateIsp(int id, IspInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var isp = await db.Isps.FirstOrDefaultAsync(x => x.IspId == id, ct);
        if (isp is null) return Results.NotFound();
        db.Record(h, "ISP_UPDATE", "isp", id, new { isp.Name, isp.SupportPhone, isp.SupportEmail }, i);
        (isp.Name, isp.SupportPhone, isp.SupportEmail) = (i.Name.Trim(), i.SupportPhone, i.SupportEmail);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveIspLink(int branchId, int? id, IspLinkInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!await db.Branches.AnyAsync(b => b.BranchId == branchId, ct)) return Results.NotFound();
        if (!await db.Isps.AnyAsync(x => x.IspId == i.IspId, ct)) errors["ispId"] = ["Unknown ISP"];
        if (i.LinkRole is not ("MPLS" or "PRIMARY" or "SECONDARY")) errors["linkRole"] = ["MPLS, PRIMARY or SECONDARY"];
        if (i.BandwidthMbps <= 0) errors["bandwidthMbps"] = ["Must be positive"];
        if (!string.IsNullOrEmpty(i.ProbeTarget) && !IPAddress.TryParse(i.ProbeTarget, out _)) errors["probeTarget"] = ["Not a valid IP address"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var link = id is { } lid ? await db.IspLinks.FirstOrDefaultAsync(l => l.IspLinkId == lid && l.BranchId == branchId, ct) : new IspLink { BranchId = branchId };
        if (link is null) return Results.NotFound();
        link.IspId = i.IspId; link.LinkRole = i.LinkRole; link.CircuitId = i.CircuitId; link.BandwidthMbps = i.BandwidthMbps;
        link.WanInterface = i.WanInterface; link.IsActive = i.IsActive;
        link.ProbeTarget = string.IsNullOrEmpty(i.ProbeTarget) ? null : IPAddress.Parse(i.ProbeTarget);
        if (id is null) db.IspLinks.Add(link);
        db.Record(h, id is null ? "ISPLINK_CREATE" : "ISPLINK_UPDATE", "isp_link", id, newValue: i);
        await InventoryEndpoints.SaveAndBumpAsync(db, [branchId], ct);
        return id is null ? Results.Created($"/api/v1/branches/{branchId}/isp-links/{link.IspLinkId}", new { link.IspLinkId }) : Results.NoContent();
    }

    private static async Task<IResult> ReplaceContacts(int branchId, ContactInput[] contacts, HttpContext h, NmsDb db, CancellationToken ct)
    {
        if (!await db.Branches.AnyAsync(b => b.BranchId == branchId, ct)) return Results.NotFound();
        if (contacts.Any(c => string.IsNullOrWhiteSpace(c.Name))) return Invalid("name", "Every contact needs a name");
        var old = await db.BranchContacts.Where(c => c.BranchId == branchId).ToListAsync(ct);
        db.BranchContacts.RemoveRange(old);
        db.BranchContacts.AddRange(contacts.Select(c => new BranchContact
        {
            BranchId = branchId, Name = c.Name.Trim(), Role = c.Role, Phone = c.Phone, Email = c.Email, IsPrimary = c.IsPrimary,
        }));
        db.Record(h, "CONTACTS_UPDATE", "branch", branchId, old.Select(c => new { c.Name, c.Phone }), contacts);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveAppCheck(int? id, AppCheckInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.Name)) errors["name"] = ["Required"];
        if (i.CheckType == "HTTP" && !Uri.TryCreate(i.Target, UriKind.Absolute, out _)) errors["target"] = ["Absolute http(s) URL required"];
        else if (i.CheckType == "TCP" && (i.Target.LastIndexOf(':') is var c && (c <= 0 || !int.TryParse(i.Target[(c + 1)..], out _))))
            errors["target"] = ["host:port required"];
        else if (i.CheckType is not ("HTTP" or "TCP")) errors["checkType"] = ["HTTP or TCP"];
        if (i.BranchId is { } bid && !await db.Branches.AnyAsync(b => b.BranchId == bid, ct)) errors["branchId"] = ["Unknown branch"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var check = id is { } cid ? await db.AppChecks.FirstOrDefaultAsync(x => x.AppCheckId == cid, ct) : new AppCheck();
        if (check is null) return Results.NotFound();
        var affected = new List<int>();
        if (check.BranchId is { } oldB) affected.Add(oldB);
        check.BranchId = i.BranchId; check.Name = i.Name.Trim(); check.CheckType = i.CheckType; check.Target = i.Target.Trim();
        check.ExpectedStatus = i.ExpectedStatus; check.TimeoutMs = i.TimeoutMs ?? 5000; check.BusinessService = i.BusinessService;
        check.IsActive = i.IsActive;
        if (id is null) db.AppChecks.Add(check);
        if (i.BranchId is { } nb) affected.Add(nb);
        else affected.AddRange(await db.Branches.Select(b => b.BranchId).ToListAsync(ct));   // global check: every agent
        db.Record(h, id is null ? "APPCHECK_CREATE" : "APPCHECK_UPDATE", "app_check", id, newValue: i);
        await InventoryEndpoints.SaveAndBumpAsync(db, [.. affected], ct);
        return id is null ? Results.Created($"/api/v1/app-checks/{check.AppCheckId}", new { check.AppCheckId }) : Results.NoContent();
    }

    private static async Task<IResult> SaveSnmpProfile(int? id, SnmpProfileInput i, HttpContext h, NmsDb db, IDataProtectionProvider dp, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.Name)) errors["name"] = ["Required"];
        if (string.IsNullOrWhiteSpace(i.SnmpUser)) errors["snmpUser"] = ["Required"];
        if (i.AuthProtocol is not ("SHA" or "SHA256")) errors["authProtocol"] = ["SHA or SHA256 (SNMPv3 authPriv only)"];
        if (i.PrivProtocol is not ("AES" or "AES256")) errors["privProtocol"] = ["AES or AES256"];
        if (id is null && (string.IsNullOrEmpty(i.AuthSecret) || string.IsNullOrEmpty(i.PrivSecret)))
            errors["secrets"] = ["Auth and privacy passphrases are required"];
        if (i.AuthSecret is { Length: > 0 and < 8 } || i.PrivSecret is { Length: > 0 and < 8 })
            errors["secrets"] = ["SNMPv3 passphrases must be at least 8 characters"];
        foreach (var (k, v) in i.Oids ?? [])
        {
            try { Cbh.Nms.Shared.OidSpec.Parse(v); }
            catch (FormatException) { errors[$"oids.{k}"] = [$"Bad OID spec '{v}'"]; }
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var p = id is { } pid ? await db.SnmpProfiles.FirstOrDefaultAsync(x => x.SnmpProfileId == pid, ct) : new SnmpProfile();
        if (p is null) return Results.NotFound();
        var protector = dp.CreateProtector(SnmpSecrets.Purpose);
        p.Name = i.Name.Trim(); p.Vendor = i.Vendor?.ToUpperInvariant(); p.SnmpUser = i.SnmpUser;
        p.AuthProtocol = i.AuthProtocol; p.PrivProtocol = i.PrivProtocol;
        if (!string.IsNullOrEmpty(i.AuthSecret)) p.AuthSecretEnc = protector.Protect(i.AuthSecret);
        if (!string.IsNullOrEmpty(i.PrivSecret)) p.PrivSecretEnc = protector.Protect(i.PrivSecret);
        p.Oids = JsonSerializer.Serialize(i.Oids ?? []);
        if (id is null) db.SnmpProfiles.Add(p);
        db.Record(h, id is null ? "SNMPPROFILE_CREATE" : "SNMPPROFILE_UPDATE", "snmp_profile", id,
            newValue: new { i.Name, i.Vendor, i.SnmpUser, i.AuthProtocol, i.PrivProtocol, i.Oids });   // never audit secrets
        var branches = id is null ? [] : await db.Devices.Where(d => d.SnmpProfileId == id).Select(d => d.BranchId).Distinct().ToArrayAsync(ct);
        await InventoryEndpoints.SaveAndBumpAsync(db, branches, ct);
        return id is null ? Results.Created($"/api/v1/snmp-profiles/{p.SnmpProfileId}", new { p.SnmpProfileId }) : Results.NoContent();
    }

    // ───────── bulk import (FR-BR-06) ─────────

    private sealed record ImportRow(int Line, string BranchCode, string DeviceType, string? Vendor, string HostName, string IpAddress,
        string? Model, string? SerialNo, string? Firmware, DateOnly? WarrantyEnd, bool IsCritical, string? ParentHostName);

    /// <summary>POST multipart file (.csv or .xlsx). dryRun=true (default) validates only.</summary>
    private static async Task<IResult> Import(IFormFile file, bool? dryRun, HttpContext h, NmsDb db, CancellationToken ct)
    {
        List<string[]> raw;
        try
        {
            await using var s = file.OpenReadStream();
            raw = file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ReadXlsx(s) : await ReadCsvAsync(s);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            return Results.Problem("Could not read the file: " + ex.Message, statusCode: 400);
        }
        if (raw.Count < 2) return Results.Problem("The file has no data rows", statusCode: 400);

        var header = raw[0].Select(x => x.Trim().ToLowerInvariant()).ToArray();
        int Col(string n) => Array.IndexOf(header, n.ToLowerInvariant());
        string? Get(string[] r, string n) => Col(n) is var i and >= 0 && i < r.Length && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
        foreach (var required in new[] { "branchCode", "deviceType", "hostName", "ipAddress" })
            if (Col(required) < 0) return Results.Problem($"Missing column '{required}'", statusCode: 400);

        var branches = await db.Branches.ToDictionaryAsync(b => b.BranchCode, b => b.BranchId, StringComparer.OrdinalIgnoreCase, ct);
        var types = await db.DeviceTypes.ToDictionaryAsync(t => t.Name, t => t.DeviceTypeId, StringComparer.OrdinalIgnoreCase, ct);
        var existing = await db.Devices.Select(d => new { d.DeviceId, d.BranchId, d.HostName, d.IpAddress }).ToListAsync(ct);

        var rows = new List<ImportRow>();
        var errors = new List<object>();
        for (var n = 1; n < raw.Count; n++)
        {
            var r = raw[n];
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var line = n + 1;
            var e = new List<string>();
            var code = Get(r, "branchCode") ?? "";
            var type = Get(r, "deviceType") ?? "";
            var host = Get(r, "hostName") ?? "";
            var ip = Get(r, "ipAddress") ?? "";
            if (!branches.ContainsKey(code)) e.Add($"Unknown branch '{code}'");
            if (!types.ContainsKey(type)) e.Add($"Unknown device type '{type}' (use {string.Join(", ", types.Keys)})");
            if (string.IsNullOrEmpty(host)) e.Add("hostName is required");
            if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Count(c => c == '.') != 3)
                e.Add($"Invalid IPv4 '{ip}'");
            else if (branches.TryGetValue(code, out var bid))
            {
                if (existing.Any(d => d.BranchId == bid && d.IpAddress.Equals(addr) && !d.HostName.Equals(host, StringComparison.OrdinalIgnoreCase)))
                    e.Add($"IP {ip} already used by another device in {code}");
                if (rows.Any(x => x.BranchCode.Equals(code, StringComparison.OrdinalIgnoreCase) && x.IpAddress == ip))
                    e.Add($"IP {ip} repeated in the file for {code}");
            }
            DateOnly? warranty = null;
            if (Get(r, "warrantyEnd") is { } w)
            {
                if (DateOnly.TryParse(w, CultureInfo.InvariantCulture, out var wd)) warranty = wd;
                else e.Add($"Bad warrantyEnd '{w}' (use yyyy-mm-dd)");
            }
            var critical = Get(r, "isCritical") is { } c && (c.Equals("true", StringComparison.OrdinalIgnoreCase) || c is "1" or "yes" or "Yes");
            if (e.Count > 0) errors.Add(new { line, errors = e });
            else rows.Add(new ImportRow(line, code, type, Get(r, "vendor"), host, ip, Get(r, "model"), Get(r, "serialNo"),
                Get(r, "firmware"), warranty, critical, Get(r, "parentHostName")));
        }

        // Parents must resolve to a device in the same branch, existing or in this file.
        foreach (var row in rows.Where(r => r.ParentHostName is not null).ToList())
        {
            var bid = branches[row.BranchCode];
            var known = existing.Any(d => d.BranchId == bid && d.HostName.Equals(row.ParentHostName, StringComparison.OrdinalIgnoreCase))
                        || rows.Any(r => r.BranchCode.Equals(row.BranchCode, StringComparison.OrdinalIgnoreCase)
                                         && r.HostName.Equals(row.ParentHostName, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                errors.Add(new { line = row.Line, errors = new[] { $"Parent '{row.ParentHostName}' not found in {row.BranchCode}" } });
                rows.Remove(row);
            }
        }

        var willCreate = rows.Count(r => !existing.Any(d => d.BranchId == branches[r.BranchCode] && d.HostName.Equals(r.HostName, StringComparison.OrdinalIgnoreCase)));
        var summary = new { totalRows = raw.Count - 1, valid = rows.Count, invalid = errors.Count, create = willCreate, update = rows.Count - willCreate, errors };
        if (dryRun != false || errors.Count > 0) return Results.Ok(new { dryRun = true, summary });

        // Apply: upsert by (branch, hostName), then parents.
        var byKey = new Dictionary<(int, string), Device>();
        foreach (var d in await db.Devices.ToListAsync(ct)) byKey[(d.BranchId, d.HostName.ToLowerInvariant())] = d;
        foreach (var r in rows)
        {
            var bid = branches[r.BranchCode];
            if (!byKey.TryGetValue((bid, r.HostName.ToLowerInvariant()), out var d))
            {
                d = new Device { BranchId = bid, HostName = r.HostName };
                db.Devices.Add(d);
                byKey[(bid, r.HostName.ToLowerInvariant())] = d;
            }
            d.DeviceTypeId = types[r.DeviceType]; d.Vendor = r.Vendor?.ToUpperInvariant(); d.IpAddress = IPAddress.Parse(r.IpAddress);
            d.Model = r.Model; d.SerialNo = r.SerialNo; d.Firmware = r.Firmware; d.WarrantyEnd = r.WarrantyEnd; d.IsCritical = r.IsCritical;
        }
        await db.SaveChangesAsync(ct);
        foreach (var r in rows.Where(r => r.ParentHostName is not null))
        {
            var bid = branches[r.BranchCode];
            var child = byKey[(bid, r.HostName.ToLowerInvariant())];
            var parent = byKey[(bid, r.ParentHostName!.ToLowerInvariant())];
            if (child.DeviceId == parent.DeviceId) continue;
            db.DeviceDependencies.RemoveRange(db.DeviceDependencies.Where(x => x.ChildDeviceId == child.DeviceId));
            db.DeviceDependencies.Add(new DeviceDependency { ChildDeviceId = child.DeviceId, ParentDeviceId = parent.DeviceId });
        }
        db.Record(h, "DEVICE_IMPORT", "device", null, newValue: new { file.FileName, summary.valid, summary.create, summary.update });
        await InventoryEndpoints.SaveAndBumpAsync(db, rows.Select(r => branches[r.BranchCode]).Distinct().ToArray(), ct);
        return Results.Ok(new { dryRun = false, summary });
    }

    private static List<string[]> ReadXlsx(Stream s)
    {
        using var wb = new XLWorkbook(s);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used is null) return [];
        return used.Rows().Select(r => r.Cells(1, used.ColumnCount()).Select(c => c.GetFormattedString()).ToArray()).ToList();
    }

    private static async Task<List<string[]>> ReadCsvAsync(Stream s)
    {
        using var reader = new StreamReader(s);
        var rows = new List<string[]>();
        while (await reader.ReadLineAsync() is { } line) rows.Add(SplitCsv(line));
        return rows;
    }

    public static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return [.. result];
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
