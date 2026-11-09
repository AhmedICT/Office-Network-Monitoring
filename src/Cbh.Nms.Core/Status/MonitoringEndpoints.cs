using System.Text.Json;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Core.Reports;
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
        view.MapGet("/dashboard/branches", Tiles);
        view.MapGet("/branches/{id:int}/latest", Latest);
        view.MapGet("/branches/{id:int}/metrics", Metrics);
        view.MapGet("/branches/{id:int}/timeline", Timeline);
    }

    // FR-DB-01: KPI row.
    private static async Task<IResult> Summary(HttpContext http, NmsDb db, BranchScope scope, NmsDbClock clock, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var branches = await db.Branches.AsNoTracking().Where(b => b.IsActive && (allowed == null || allowed.Contains(b.BranchId)))
            .Select(b => b.BranchId).ToListAsync(ct);
        var statuses = await db.StatusCurrent.AsNoTracking()
            .Where(s => s.EntityType == "BRANCH" && branches.Contains(s.EntityId)).ToListAsync(ct);
        int C(string st) => statuses.Count(s => s.Status == st);

        // Availability today (Asia/Dhaka midnight → now), averaged over the branches in scope.
        var (dayStart, now) = clock.TodayUtc();
        var history = await db.StatusHistory.AsNoTracking()
            .Where(h => h.EntityType == "BRANCH" && branches.Contains(h.EntityId) && (h.EndedAt == null || h.EndedAt > dayStart))
            .ToListAsync(ct);
        var maint = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.BranchId != null && branches.Contains(w.BranchId.Value) && w.EndsAt > dayStart && w.StartsAt < now).ToListAsync(ct);
        var avail = branches.Count == 0 ? 100 : branches.Average(id => AvailabilityCalculator.Compute(dayStart, now,
            history.Where(h => h.EntityId == id).Select(h => new StatusInterval(h.Status, h.StartedAt, h.EndedAt)),
            maint.Where(m => m.BranchId == id).Select(m => new Window(m.StartsAt, m.EndsAt))).AvailabilityPct);

        return Results.Ok(new
        {
            totalBranches = branches.Count,
            online = C(BranchStatus.Online),
            degraded = C(BranchStatus.Degraded),
            offlineIsp = C(BranchStatus.OfflineIsp),
            offlinePower = C(BranchStatus.OfflinePower),
            unknown = branches.Count - C(BranchStatus.Online) - C(BranchStatus.Degraded) - C(BranchStatus.OfflineIsp) - C(BranchStatus.OfflinePower),
            availabilityTodayPct = Math.Round(avail, 2),
            openIncidents = await db.Incidents.CountAsync(i => IncidentStatus.Active.Contains(i.Status) && branches.Contains(i.BranchId), ct),
            unackedIncidents = await db.Incidents.CountAsync(i => i.Status == IncidentStatus.Open && branches.Contains(i.BranchId), ct),
            activeAlerts = await db.Alerts.CountAsync(a => a.ClearedAt == null && !a.IsSuppressed && branches.Contains(a.BranchId), ct),
            unackedCritical = await db.Alerts.CountAsync(a => a.ClearedAt == null && !a.IsSuppressed && a.AckAt == null
                                                              && a.Severity == Severity.Critical && branches.Contains(a.BranchId), ct),
            asOfUtc = now,
        });
    }

    /// <summary>SCR-03 cards and device table: the newest value of every metric the branch's agent reports.</summary>
    private static async Task<IResult> Latest(int id, HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (!await scope.CanSeeAsync(http.User, id, ct)) return Results.NotFound();
        await using var cmd = ds.CreateCommand("""
            SELECT DISTINCT ON (m.entity_type, m.entity_id, m.metric_key) m.entity_type, m.entity_id, m.metric_key, m.value, m.time
            FROM mon.metric_raw m JOIN inv.agent a ON a.agent_id = m.agent_id
            WHERE a.branch_id = $1 AND m.time > now() - interval '1 hour'
            ORDER BY m.entity_type, m.entity_id, m.metric_key, m.time DESC
            """);
        cmd.Parameters.AddWithValue(id);
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new { entity = r.GetString(0), id = r.GetInt32(1), key = r.GetString(2), value = r.GetDouble(3), time = r.GetFieldValue<DateTimeOffset>(4) });
        return Results.Ok(new { items });
    }

    /// <summary>SCR-02 tile grid / §7.4: status plus the small indicators each tile shows.</summary>
    private static async Task<IResult> Tiles(HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        await using var cmd = ds.CreateCommand("""
            WITH latest AS (
                SELECT DISTINCT ON (m.entity_type, m.entity_id, m.metric_key, a.branch_id)
                       a.branch_id, m.entity_type, m.entity_id, m.metric_key, m.value
                FROM mon.metric_raw m JOIN inv.agent a ON a.agent_id = m.agent_id
                WHERE m.time > now() - interval '30 minutes'
                  AND m.metric_key IN ('up', 'ups_on_battery', 'ups_battery_pct')
                  AND m.entity_type IN ('ISPLINK', 'DEVICE')
                ORDER BY m.entity_type, m.entity_id, m.metric_key, a.branch_id, m.time DESC),
            lat AS (
                SELECT entity_id AS branch_id, avg(value) AS latency
                FROM mon.metric_raw WHERE entity_type = 'BRANCH' AND metric_key = 'latency_ms' AND time > now() - interval '10 minutes'
                GROUP BY entity_id)
            SELECT b.branch_id, b.branch_code, b.branch_name, r.name, b.tier,
                   coalesce(s.status, 'UNKNOWN'), s.since, s.detail->>'reason', lat.latency,
                   (SELECT json_agg(json_build_object('role', l.link_role, 'isp', i.name,
                           'up', (SELECT x.value FROM latest x WHERE x.entity_type = 'ISPLINK' AND x.entity_id = l.isp_link_id
                                  AND x.metric_key = 'up' LIMIT 1)) ORDER BY l.link_role DESC)
                      FROM org.isp_link l JOIN org.isp i ON i.isp_id = l.isp_id
                     WHERE l.branch_id = b.branch_id AND l.is_active AND l.link_role <> 'MPLS')::text,
                   (SELECT max(x.value) FROM latest x WHERE x.branch_id = b.branch_id AND x.metric_key = 'ups_on_battery'),
                   (SELECT min(x.value) FROM latest x WHERE x.branch_id = b.branch_id AND x.metric_key = 'ups_battery_pct'),
                   (SELECT i.incident_no FROM ops.incident i WHERE i.branch_id = b.branch_id AND i.status IN ('OPEN','ACK','IN_PROGRESS')
                     ORDER BY i.detected_at DESC LIMIT 1)
            FROM org.branch b JOIN org.region r ON r.region_id = b.region_id
            LEFT JOIN mon.status_current s ON s.entity_type = 'BRANCH' AND s.entity_id = b.branch_id
            LEFT JOIN lat ON lat.branch_id = b.branch_id
            WHERE b.is_active AND ($1::int[] IS NULL OR b.branch_id = ANY($1))
            ORDER BY b.branch_code
            """);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = allowed is null ? DBNull.Value : allowed.ToArray(),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer,
        });
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new
            {
                branchId = r.GetInt32(0), code = r.GetString(1), name = r.GetString(2), region = r.GetString(3), tier = r.GetInt16(4),
                status = r.GetString(5), statusSinceUtc = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6),
                reason = r.IsDBNull(7) ? null : r.GetString(7),
                latencyMs = r.IsDBNull(8) ? (double?)null : Math.Round(r.GetDouble(8), 1),
                isps = r.IsDBNull(9) ? (JsonElement?)null : JsonDocument.Parse(r.GetString(9)).RootElement.Clone(),
                ups = r.IsDBNull(10) && r.IsDBNull(11) ? null : new
                {
                    onBattery = !r.IsDBNull(10) && r.GetDouble(10) >= 1,
                    batteryPct = r.IsDBNull(11) ? (double?)null : r.GetDouble(11),
                },
                openIncidentNo = r.IsDBNull(12) ? null : r.GetString(12),
            });
        }
        return Results.Ok(new { items });
    }

    /// <summary>GET /branches/{id}/metrics?key=&amp;entity=&amp;entityId=&amp;from=&amp;to=&amp;grain=raw|hour|day (raw ≤ 7 days).</summary>
    private static async Task<IResult> Metrics(int id, string key, string? entity, int? entityId, DateTimeOffset? from,
        DateTimeOffset? to, string? grain, HttpContext http, BranchScope scope, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (!await scope.CanSeeAsync(http.User, id, ct)) return Results.NotFound();
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddHours(-24)).ToUniversalTime();
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
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddHours(-24)).ToUniversalTime();
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
