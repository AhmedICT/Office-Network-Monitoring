using System.Net;
using System.Security.Cryptography;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Inventory;

public sealed record BranchInput(
    string BranchCode, string BranchName, int RegionId, string? Address, decimal? Latitude, decimal? Longitude,
    short Tier, TimeOnly? OpenTime, TimeOnly? CloseTime, string? MplsRouterIp, decimal? SlaTargetPct, bool IsActive = true);

public sealed record DeviceInput(
    int BranchId, short DeviceTypeId, string? Vendor, string HostName, string IpAddress, string? Model,
    string? SerialNo, string? Firmware, DateOnly? WarrantyEnd, int? SnmpProfileId, short? PollIntervalS,
    bool IsCritical, int[]? ParentDeviceIds, bool IsActive = true);

public sealed record EnrollTokenRequest(int BranchId);

/// <summary>Branches, devices and agents (FR-BR-01..05, SCR-04). Saving bumps the branch config version
/// so its agent pulls the change within 5 minutes.</summary>
public static class InventoryEndpoints
{
    public static void MapInventoryApi(this IEndpointRouteBuilder api)
    {
        var view = api.MapGroup("").RequireAuthorization(AuthPolicies.Viewer);
        var edit = api.MapGroup("").RequireAuthorization(AuthPolicies.EditInventory);

        view.MapGet("/regions", (NmsDb db) => db.Regions.AsNoTracking().OrderBy(r => r.Name).ToListAsync());
        view.MapGet("/branches", ListBranches);
        view.MapGet("/branches/{id:int}", GetBranch);
        edit.MapPost("/branches", CreateBranch);
        edit.MapPut("/branches/{id:int}", UpdateBranch);

        view.MapGet("/devices", ListDevices);
        edit.MapPost("/devices", CreateDevice);
        edit.MapPut("/devices/{id:int}", UpdateDevice);

        edit.MapGet("/agents", ListAgents);
        edit.MapPost("/agents/{id:int}/revoke", RevokeAgent);
        edit.MapPost("/agents/enroll-token", CreateEnrollToken);
    }

    // ───────── branches ─────────

    private static async Task<IResult> ListBranches(HttpContext http, NmsDb db, BranchScope scope,
        int? region, string? status, short? tier, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var q = from b in db.Branches.AsNoTracking()
                join s in db.StatusCurrent.Where(s => s.EntityType == "BRANCH") on b.BranchId equals s.EntityId into sj
                from s in sj.DefaultIfEmpty()
                where allowed == null || allowed.Contains(b.BranchId)
                select new
                {
                    b.BranchId, Code = b.BranchCode, Name = b.BranchName, Region = b.Region!.Name, b.RegionId, b.Tier,
                    b.IsActive, Status = s != null ? s.Status : "UNKNOWN", StatusSinceUtc = s != null ? s.Since : (DateTimeOffset?)null,
                    StatusDetail = s != null ? s.Detail : null,
                };
        if (region is { } r) q = q.Where(x => x.RegionId == r);
        if (tier is { } t) q = q.Where(x => x.Tier == t);
        if (!string.IsNullOrEmpty(status))
        {
            var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(x => statuses.Contains(x.Status));
        }
        var items = await q.OrderBy(x => x.Code).ToListAsync(ct);
        return Results.Ok(new { items, total = items.Count });
    }

    private static async Task<IResult> GetBranch(int id, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        if (!await scope.CanSeeAsync(http.User, id, ct)) return Results.NotFound();
        var b = await db.Branches.AsNoTracking().Include(x => x.Region).Include(x => x.Contacts)
            .Include(x => x.IspLinks).ThenInclude(l => l.Isp).FirstOrDefaultAsync(x => x.BranchId == id, ct);
        if (b is null) return Results.NotFound();

        var status = await db.StatusCurrent.AsNoTracking().FirstOrDefaultAsync(s => s.EntityType == "BRANCH" && s.EntityId == id, ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.BranchId == id && !a.IsRevoked)
            .Select(a => new { a.AgentId, a.HostName, a.AgentVersion, a.ConfigVersion, a.LastSeenAt, a.BufferRows }).ToListAsync(ct);
        var configVersion = await db.BranchConfigVersions.Where(v => v.BranchId == id).Select(v => v.Version).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            b.BranchId, Code = b.BranchCode, Name = b.BranchName, Region = b.Region!.Name, b.RegionId, b.Address,
            b.Latitude, b.Longitude, b.Tier, b.OpenTime, b.CloseTime, MplsRouterIp = b.MplsRouterIp?.ToString(),
            b.SlaTargetPct, b.IsActive,
            Status = status?.Status ?? "UNKNOWN", StatusSinceUtc = status?.Since, StatusDetail = status?.Detail,
            Contacts = b.Contacts.Select(c => new { c.Name, c.Role, c.Phone, c.Email, c.IsPrimary }),
            IspLinks = b.IspLinks.Select(l => new
            {
                l.IspLinkId, Isp = l.Isp!.Name, IspSupportPhone = l.Isp.SupportPhone, l.LinkRole, l.CircuitId,
                l.BandwidthMbps, l.WanInterface, ProbeTarget = l.ProbeTarget?.ToString(), l.IsActive,
            }),
            Agents = agents,
            ConfigVersion = configVersion,
        });
    }

    private static async Task<IResult> CreateBranch(BranchInput input, HttpContext http, NmsDb db, CancellationToken ct)
    {
        if (await ValidateBranch(input, null, db, ct) is { } problem) return problem;
        var b = new Branch();
        Apply(input, b);
        db.Branches.Add(b);
        await db.SaveChangesAsync(ct);
        db.Record(http, "BRANCH_CREATE", "branch", b.BranchId, newValue: input);
        await SaveAndBumpAsync(db, [b.BranchId], ct);
        return Results.Created($"/api/v1/branches/{b.BranchId}", new { b.BranchId });
    }

    private static async Task<IResult> UpdateBranch(int id, BranchInput input, HttpContext http, NmsDb db, CancellationToken ct)
    {
        var b = await db.Branches.FirstOrDefaultAsync(x => x.BranchId == id, ct);
        if (b is null) return Results.NotFound();
        if (await ValidateBranch(input, id, db, ct) is { } problem) return problem;
        var old = ToInput(b);
        Apply(input, b);
        db.Record(http, "BRANCH_UPDATE", "branch", id, old, input);
        await SaveAndBumpAsync(db, [id], ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateBranch(BranchInput i, int? id, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.BranchCode) || i.BranchCode.Length > 10) errors["branchCode"] = ["Required, max 10 characters"];
        else if (await db.Branches.AnyAsync(b => b.BranchCode == i.BranchCode && b.BranchId != id, ct)) errors["branchCode"] = ["Branch code already exists"];
        if (string.IsNullOrWhiteSpace(i.BranchName)) errors["branchName"] = ["Required"];
        if (i.Tier is < 1 or > 3) errors["tier"] = ["Tier must be 1, 2 or 3"];
        if (!await db.Regions.AnyAsync(r => r.RegionId == i.RegionId, ct)) errors["regionId"] = ["Unknown region"];
        if (!string.IsNullOrEmpty(i.MplsRouterIp) && !IsIPv4(i.MplsRouterIp)) errors["mplsRouterIp"] = ["Not a valid IPv4 address"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static void Apply(BranchInput i, Branch b)
    {
        b.BranchCode = i.BranchCode.Trim().ToUpperInvariant();
        b.BranchName = i.BranchName.Trim();
        b.RegionId = i.RegionId;
        b.Address = i.Address;
        b.Latitude = i.Latitude;
        b.Longitude = i.Longitude;
        b.Tier = i.Tier;
        b.OpenTime = i.OpenTime;
        b.CloseTime = i.CloseTime;
        b.MplsRouterIp = string.IsNullOrEmpty(i.MplsRouterIp) ? null : IPAddress.Parse(i.MplsRouterIp);
        b.SlaTargetPct = i.SlaTargetPct ?? 99.50m;
        b.IsActive = i.IsActive;
    }

    private static BranchInput ToInput(Branch b) => new(b.BranchCode, b.BranchName, b.RegionId, b.Address, b.Latitude,
        b.Longitude, b.Tier, b.OpenTime, b.CloseTime, b.MplsRouterIp?.ToString(), b.SlaTargetPct, b.IsActive);

    // ───────── devices ─────────

    private static async Task<IResult> ListDevices(HttpContext http, NmsDb db, BranchScope scope, int? branchId, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var q = db.Devices.AsNoTracking().Where(d => allowed == null || allowed.Contains(d.BranchId));
        if (branchId is { } bid) q = q.Where(d => d.BranchId == bid);
        var devices = await q.OrderBy(d => d.BranchId).ThenBy(d => d.HostName)
            .Select(d => new
            {
                d.DeviceId, d.BranchId, Type = d.DeviceType!.Name, d.DeviceTypeId, d.Vendor, d.HostName,
                IpAddress = d.IpAddress.ToString(), d.Model, d.SerialNo, d.Firmware, d.WarrantyEnd,
                d.SnmpProfileId, d.PollIntervalS, d.IsCritical, d.IsActive,
            }).ToListAsync(ct);
        var ids = devices.Select(d => d.DeviceId).ToList();
        var parents = await db.DeviceDependencies.AsNoTracking().Where(x => ids.Contains(x.ChildDeviceId)).ToListAsync(ct);
        return Results.Ok(new
        {
            items = devices.Select(d => new
            {
                d.DeviceId, d.BranchId, d.Type, d.DeviceTypeId, d.Vendor, d.HostName, d.IpAddress, d.Model, d.SerialNo,
                d.Firmware, d.WarrantyEnd, d.SnmpProfileId, d.PollIntervalS, d.IsCritical, d.IsActive,
                ParentDeviceIds = parents.Where(p => p.ChildDeviceId == d.DeviceId).Select(p => p.ParentDeviceId),
            }),
            total = devices.Count,
        });
    }

    private static async Task<IResult> CreateDevice(DeviceInput input, HttpContext http, NmsDb db, CancellationToken ct)
    {
        if (await ValidateDevice(input, null, db, ct) is { } problem) return problem;
        var d = new Device();
        Apply(input, d);
        db.Devices.Add(d);
        await db.SaveChangesAsync(ct);
        SetParents(db, d.DeviceId, input.ParentDeviceIds);
        db.Record(http, "DEVICE_CREATE", "device", d.DeviceId, newValue: input);
        await SaveAndBumpAsync(db, [d.BranchId], ct);
        return Results.Created($"/api/v1/devices/{d.DeviceId}", new { d.DeviceId });
    }

    private static async Task<IResult> UpdateDevice(int id, DeviceInput input, HttpContext http, NmsDb db, CancellationToken ct)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.DeviceId == id, ct);
        if (d is null) return Results.NotFound();
        if (await ValidateDevice(input, id, db, ct) is { } problem) return problem;

        var oldParents = await db.DeviceDependencies.Where(x => x.ChildDeviceId == id).ToListAsync(ct);
        var old = new { d.BranchId, d.DeviceTypeId, d.HostName, IpAddress = d.IpAddress.ToString(), d.IsCritical, d.IsActive,
                        ParentDeviceIds = oldParents.Select(p => p.ParentDeviceId) };
        var oldBranch = d.BranchId;
        Apply(input, d);
        db.DeviceDependencies.RemoveRange(oldParents);
        SetParents(db, id, input.ParentDeviceIds);
        db.Record(http, "DEVICE_UPDATE", "device", id, old, input);
        await SaveAndBumpAsync(db, [d.BranchId, oldBranch], ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateDevice(DeviceInput i, int? id, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!await db.Branches.AnyAsync(b => b.BranchId == i.BranchId, ct)) errors["branchId"] = ["Unknown branch"];
        if (!await db.DeviceTypes.AnyAsync(t => t.DeviceTypeId == i.DeviceTypeId, ct)) errors["deviceTypeId"] = ["Unknown device type"];
        if (string.IsNullOrWhiteSpace(i.HostName)) errors["hostName"] = ["Required"];
        if (!IsIPv4(i.IpAddress)) errors["ipAddress"] = ["Not a valid IPv4 address"];
        else
        {
            var ip = IPAddress.Parse(i.IpAddress);
            if (await db.Devices.AnyAsync(d => d.BranchId == i.BranchId && d.IpAddress == ip && d.DeviceId != id, ct))
                errors["ipAddress"] = ["Another device in this branch already has this IP"];
        }
        if (i.SnmpProfileId is { } sp && !await db.SnmpProfiles.AnyAsync(p => p.SnmpProfileId == sp, ct))
            errors["snmpProfileId"] = ["Unknown SNMP profile"];
        if (i.PollIntervalS is < 15 or > 3600) errors["pollIntervalS"] = ["Between 15 and 3600 seconds"];

        if (i.ParentDeviceIds is { Length: > 0 } parents)
        {
            var parentBranches = await db.Devices.Where(d => parents.Contains(d.DeviceId))
                .Select(d => new { d.DeviceId, d.BranchId }).ToListAsync(ct);
            if (parentBranches.Count != parents.Distinct().Count()) errors["parentDeviceIds"] = ["Unknown parent device"];
            else if (parentBranches.Any(p => p.BranchId != i.BranchId)) errors["parentDeviceIds"] = ["Parent must be in the same branch"];
            else if (id is { } childId)
            {
                var edges = (await db.DeviceDependencies.AsNoTracking().Where(x => x.ChildDeviceId != childId).ToListAsync(ct))
                    .GroupBy(x => x.ChildDeviceId).ToDictionary(g => g.Key, g => g.Select(x => x.ParentDeviceId).ToList());
                if (parents.Any(p => DependencyGraph.WouldCreateCycle(childId, p, edges)))
                    errors["parentDeviceIds"] = ["This parent would create a dependency cycle"];
            }
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static void Apply(DeviceInput i, Device d)
    {
        d.BranchId = i.BranchId;
        d.DeviceTypeId = i.DeviceTypeId;
        d.Vendor = i.Vendor?.Trim().ToUpperInvariant();
        d.HostName = i.HostName.Trim();
        d.IpAddress = IPAddress.Parse(i.IpAddress);
        d.Model = i.Model;
        d.SerialNo = i.SerialNo;
        d.Firmware = i.Firmware;
        d.WarrantyEnd = i.WarrantyEnd;
        d.SnmpProfileId = i.SnmpProfileId;
        d.PollIntervalS = i.PollIntervalS ?? 60;
        d.IsCritical = i.IsCritical;
        d.IsActive = i.IsActive;
    }

    private static void SetParents(NmsDb db, int deviceId, int[]? parentIds)
    {
        foreach (var p in (parentIds ?? []).Distinct())
            db.DeviceDependencies.Add(new DeviceDependency { ChildDeviceId = deviceId, ParentDeviceId = p });
    }

    // ───────── agents ─────────

    private static async Task<IResult> ListAgents(NmsDb db, CancellationToken ct)
    {
        var agents = await (from a in db.Agents.AsNoTracking()
                            join b in db.Branches on a.BranchId equals b.BranchId
                            join v in db.BranchConfigVersions on a.BranchId equals v.BranchId into vj
                            from v in vj.DefaultIfEmpty()
                            orderby b.BranchCode
                            select new
                            {
                                a.AgentId, a.BranchId, BranchCode = b.BranchCode, BranchName = b.BranchName, a.HostName,
                                a.AgentVersion, a.ConfigVersion, LatestConfigVersion = v != null ? v.Version : 0,
                                a.LastSeenAt, a.BufferRows, a.RegisteredAt, a.IsRevoked,
                            }).ToListAsync(ct);
        return Results.Ok(new { items = agents, total = agents.Count });
    }

    private static async Task<IResult> RevokeAgent(int id, HttpContext http, NmsDb db, CancellationToken ct)
    {
        var a = await db.Agents.FirstOrDefaultAsync(x => x.AgentId == id, ct);
        if (a is null) return Results.NotFound();
        a.IsRevoked = true;
        db.Record(http, "AGENT_REVOKE", "agent", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateEnrollToken(EnrollTokenRequest req, HttpContext http, NmsDb db, CancellationToken ct)
    {
        if (!await db.Branches.AnyAsync(b => b.BranchId == req.BranchId, ct)) return Results.NotFound();
        var token = AgentAuthHandler.NewSecret();
        var expires = DateTimeOffset.UtcNow.AddHours(24);
        db.EnrollTokens.Add(new EnrollToken
        {
            TokenHash = AgentAuthHandler.HashKey(token), BranchId = req.BranchId,
            CreatedBy = http.User.Identity?.Name ?? "unknown", ExpiresAt = expires,
        });
        db.Record(http, "AGENT_ENROLL_TOKEN", "branch", req.BranchId);
        await db.SaveChangesAsync(ct);
        // Shown once; only its hash is stored.
        return Results.Ok(new { token, expiresAtUtc = expires });
    }

    // ───────── helpers ─────────

    /// <summary>Saves pending changes and bumps the branches' config version in one transaction, so an
    /// agent can never see the new version number paired with the old data.</summary>
    public static async Task SaveAndBumpAsync(NmsDb db, int[] branchIds, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        foreach (var branchId in branchIds.Distinct())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inv.branch_config_version (branch_id, version, changed_at) VALUES ({branchId}, 1, now())
                ON CONFLICT (branch_id) DO UPDATE SET version = inv.branch_config_version.version + 1, changed_at = now()
                """, ct);
        await tx.CommitAsync(ct);
    }

    private static bool IsIPv4(string? s) =>
        IPAddress.TryParse(s, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && s!.Count(c => c == '.') == 3;
}
