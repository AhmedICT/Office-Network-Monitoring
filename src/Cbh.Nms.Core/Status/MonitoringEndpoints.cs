using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cbh.Nms.Core.Status;

public static class MonitoringEndpoints
{
    public static void MapMonitoringApi(this IEndpointRouteBuilder api)
    {
        var view = api.MapGroup("").RequireAuthorization(AuthPolicies.Viewer);
        view.MapGet("/dashboard/summary", Summary);
        view.MapGet("/branches/{id:int}/metrics", Metrics);
        view.MapGet("/branches/{id:int}/timeline", Timeline);
    }

    // FR-DB-01. Availability-today and incident counts join here once those modules land.
    private static async Task<IResult> Summary(HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var branches = db.Branches.Where(b => b.IsActive && (allowed == null || allowed.Contains(b.BranchId)));
        var total = await branches.CountAsync(ct);
        var byStatus = await (from b in branches
                              join s in db.StatusCurrent.Where(s => s.EntityType == "BRANCH") on b.BranchId equals s.EntityId
                              group s by s.Status into g
                              select new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        int C(string s) => byStatus.GetValueOrDefault(s);
        return Results.Ok(new
        {
            totalBranches = total,
            online = C(BranchStatus.Online),
            degraded = C(BranchStatus.Degraded),
            offlineIsp = C(BranchStatus.OfflineIsp),
            offlinePower = C(BranchStatus.OfflinePower),
            unknown = total - C(BranchStatus.Online) - C(BranchStatus.Degraded) - C(BranchStatus.OfflineIsp) - C(BranchStatus.OfflinePower),
            asOfUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>GET /branches/{id}/metrics?key=&amp;entity=&amp;entityId=&amp;from=&amp;to=&amp;grain=raw|hour|day (raw ≤ 7 days).</summary>
    private static async Task<IResult> Metrics(int id, string key, string? entity, int? entityId, DateTimeOffset? from,
        DateTimeOffset? to, string? grain, HttpContext http, BranchScope scope, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (!await scope.CanSeeAsync(http.User, id, ct)) return Results.NotFound();
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-24);
        grain ??= "raw";
        if (start >= end) return Results.Problem("from must be before to", statusCode: 400);
        if (grain == "raw" && end - start > TimeSpan.FromDays(7)) return Results.Problem("raw grain is limited to 7 days", statusCode: 400);

        entity ??= EntityTypes.Branch;
        var eid = entityId ?? id;
        if (!await EntityBelongsToBranch(db, entity, eid, id, ct)) return Results.NotFound();

        var sql = grain switch
        {
            "raw" => "SELECT time, value, value, value FROM mon.metric_raw WHERE entity_type=$1 AND entity_id=$2 AND metric_key=$3 AND time >= $4 AND time < $5 ORDER BY time",
            "hour" => "SELECT bucket, avg_value, min_value, max_value FROM mon.metric_hourly WHERE entity_type=$1 AND entity_id=$2 AND metric_key=$3 AND bucket >= $4 AND bucket < $5 ORDER BY bucket",
            "day" => "SELECT bucket, avg_value, min_value, max_value FROM mon.metric_daily WHERE entity_type=$1 AND entity_id=$2 AND metric_key=$3 AND bucket >= $4 AND bucket < $5 ORDER BY bucket",
            _ => null,
        };
        if (sql is null) return Results.Problem("grain must be raw, hour or day", statusCode: 400);

        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue(entity); cmd.Parameters.AddWithValue(eid); cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(start); cmd.Parameters.AddWithValue(end);
        var points = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            points.Add(new { t = r.GetFieldValue<DateTimeOffset>(0), avg = r.GetDouble(1), min = r.GetDouble(2), max = r.GetDouble(3) });
        return Results.Ok(new { entity, entityId = eid, key, grain, points });
    }

    private static async Task<IResult> Timeline(int id, DateTimeOffset? from, DateTimeOffset? to, HttpContext http,
        BranchScope scope, NmsDb db, CancellationToken ct)
    {
        if (!await scope.CanSeeAsync(http.User, id, ct)) return Results.NotFound();
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddHours(-24);
        var rows = await db.StatusHistory.AsNoTracking()
            .Where(h => h.EntityType == "BRANCH" && h.EntityId == id && h.StartedAt < end && (h.EndedAt == null || h.EndedAt > start))
            .OrderBy(h => h.StartedAt)
            .Select(h => new { h.Status, h.StartedAt, h.EndedAt, h.InMaintenance })
            .ToListAsync(ct);
        return Results.Ok(new { items = rows });
    }

    private static Task<bool> EntityBelongsToBranch(NmsDb db, string entity, int entityId, int branchId, CancellationToken ct) =>
        entity switch
        {
            EntityTypes.Branch => Task.FromResult(entityId == branchId),
            EntityTypes.Device => db.Devices.AnyAsync(d => d.DeviceId == entityId && d.BranchId == branchId, ct),
            EntityTypes.IspLink => db.IspLinks.AnyAsync(l => l.IspLinkId == entityId && l.BranchId == branchId, ct),
            EntityTypes.App => db.AppChecks.AnyAsync(c => c.AppCheckId == entityId && (c.BranchId == branchId || c.BranchId == null), ct),
            _ => Task.FromResult(false),
        };
}
