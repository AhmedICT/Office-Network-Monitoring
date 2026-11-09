using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Status;

public sealed record MaintenanceInput(int? BranchId, int? DeviceId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string Reason, string? ChangeRequestNo);

/// <summary>SCR-08 / FR-AL-05. Windows longer than 24 h need Head of IT approval before they take effect.</summary>
public static class MaintenanceEndpoints
{
    public static readonly TimeSpan MaxWithoutApproval = TimeSpan.FromHours(24);

    public static void MapMaintenanceApi(this IEndpointRouteBuilder api)
    {
        api.MapGet("/maintenance", List).RequireAuthorization(AuthPolicies.Viewer);
        api.MapPost("/maintenance", Create).RequireAuthorization(AuthPolicies.Maintenance);
        api.MapPost("/maintenance/{id:int}/approve", Approve).RequireAuthorization(AuthPolicies.ApproveMaintenance);
        api.MapDelete("/maintenance/{id:int}", End).RequireAuthorization(AuthPolicies.Maintenance);
    }

    private static async Task<IResult> List(HttpContext http, NmsDb db, BranchScope scope, bool? includePast, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var now = DateTimeOffset.UtcNow;
        var q = from w in db.MaintenanceWindows.AsNoTracking()
                join d in db.Devices on w.DeviceId equals d.DeviceId into dj
                from d in dj.DefaultIfEmpty()
                let branchId = w.BranchId ?? d.BranchId
                join b in db.Branches on branchId equals b.BranchId
                join cu in db.Users on w.CreatedBy equals cu.UserId
                join au in db.Users on w.ApprovedBy equals au.UserId into aj
                from au in aj.DefaultIfEmpty()
                where (allowed == null || allowed.Contains(branchId)) && (includePast == true || w.EndsAt > now)
                orderby w.StartsAt descending
                select new
                {
                    w.MaintenanceId, BranchId = branchId, BranchCode = b.BranchCode, BranchName = b.BranchName,
                    w.DeviceId, Device = d != null ? d.HostName : null, w.StartsAt, w.EndsAt, w.Reason, w.ChangeRequestNo,
                    CreatedBy = cu.DisplayName, ApprovedBy = au != null ? au.DisplayName : null,
                    NeedsApproval = w.ApprovedBy == null && w.EndsAt - w.StartsAt > MaxWithoutApproval,
                    IsActive = w.StartsAt <= now && w.EndsAt > now,
                };
        return Results.Ok(new { items = await q.Take(500).ToListAsync(ct) });
    }

    private static async Task<IResult> Create(MaintenanceInput input, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.BranchId is null && input.DeviceId is null) errors["target"] = ["Choose a branch or a device"];
        if (input.EndsAt <= input.StartsAt) errors["endsAt"] = ["End must be after start"];
        if (input.EndsAt <= DateTimeOffset.UtcNow) errors["endsAt"] = ["End must be in the future"];
        if (string.IsNullOrWhiteSpace(input.Reason)) errors["reason"] = ["Reason is required"];

        int? branchId = input.BranchId;
        if (input.DeviceId is { } did)
        {
            var dev = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == did, ct);
            if (dev is null) errors["deviceId"] = ["Unknown device"];
            else if (input.BranchId is { } bid && bid != dev.BranchId) errors["deviceId"] = ["Device is not in that branch"];
            else branchId = dev.BranchId;
        }
        else if (branchId is { } b && !await db.Branches.AnyAsync(x => x.BranchId == b, ct)) errors["branchId"] = ["Unknown branch"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (!await scope.CanSeeAsync(http.User, branchId!.Value, ct)) return Results.Forbid();

        var userId = http.User.UserId()!.Value;
        var longWindow = input.EndsAt - input.StartsAt > MaxWithoutApproval;
        var canSelfApprove = http.User.IsInRole(Roles.HeadOfIt) || http.User.IsInRole(Roles.SuperAdmin);
        var w = new MaintenanceWindow
        {
            BranchId = input.DeviceId is null ? input.BranchId : null, DeviceId = input.DeviceId,
            StartsAt = input.StartsAt, EndsAt = input.EndsAt, Reason = input.Reason.Trim(), ChangeRequestNo = input.ChangeRequestNo,
            CreatedBy = userId, ApprovedBy = longWindow && canSelfApprove ? userId : null,
        };
        db.MaintenanceWindows.Add(w);
        db.Record(http, "MAINTENANCE_CREATE", "maintenance", null, newValue: input);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/maintenance/{w.MaintenanceId}",
            new { w.MaintenanceId, needsApproval = longWindow && w.ApprovedBy is null });
    }

    private static async Task<IResult> Approve(int id, HttpContext http, NmsDb db, CancellationToken ct)
    {
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.MaintenanceId == id, ct);
        if (w is null) return Results.NotFound();
        w.ApprovedBy = http.User.UserId();
        db.Record(http, "MAINTENANCE_APPROVE", "maintenance", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Ends a running window now, or deletes one that has not started.</summary>
    private static async Task<IResult> End(int id, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.MaintenanceId == id, ct);
        if (w is null) return Results.NotFound();
        var branchId = w.BranchId ?? await db.Devices.Where(d => d.DeviceId == w.DeviceId).Select(d => d.BranchId).FirstAsync(ct);
        if (!await scope.CanSeeAsync(http.User, branchId, ct)) return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        if (w.StartsAt > now) db.MaintenanceWindows.Remove(w);
        else if (w.EndsAt > now) w.EndsAt = now;
        db.Record(http, "MAINTENANCE_END", "maintenance", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
