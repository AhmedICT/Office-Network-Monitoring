using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cbh.Nms.Core.Reports;

/// <summary>A report as a plain table, so every report exports the same way to JSON, Excel and PDF.</summary>
public sealed record ReportTable(string Title, string Subtitle, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows)
{
    /// <summary>Row indexes to highlight (e.g. below SLA target).</summary>
    public IReadOnlyList<int> Flagged { get; init; } = [];
}

public sealed class ReportService(NmsDb db, NpgsqlDataSource ds, IOptions<NotificationOptions> opt)
{
    private TimeZoneInfo Tz => TimeZoneInfo.FindSystemTimeZoneById(opt.Value.TimeZone);

    /// <summary>Month boundaries in Asia/Dhaka, as UTC instants; an in-progress month ends now.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) MonthRange(int year, int month)
    {
        var offset = Tz.GetUtcOffset(new DateTime(year, month, 1));
        var start = new DateTimeOffset(year, month, 1, 0, 0, 0, offset);
        var end = start.AddMonths(1);
        var now = DateTimeOffset.UtcNow;
        // Npgsql only accepts UTC instants for timestamptz.
        return (start.ToUniversalTime(), (end < now ? end : now).ToUniversalTime());
    }

    private async Task<List<Branch>> BranchesAsync(HashSet<int>? allowed, int? regionId, int? branchId, CancellationToken ct) =>
        await db.Branches.AsNoTracking().Include(b => b.Region)
            .Where(b => b.IsActive && (allowed == null || allowed.Contains(b.BranchId))
                        && (regionId == null || b.RegionId == regionId) && (branchId == null || b.BranchId == branchId))
            .OrderBy(b => b.BranchCode).ToListAsync(ct);

    /// <summary>FR-RP-01 / FR-AV-06.</summary>
    public async Task<ReportTable> AvailabilityAsync(int year, int month, HashSet<int>? allowed, int? regionId, int? branchId, CancellationToken ct)
    {
        var (start, end) = MonthRange(year, month);
        var branches = await BranchesAsync(allowed, regionId, branchId, ct);
        var ids = branches.Select(b => b.BranchId).ToList();

        var history = await db.StatusHistory.AsNoTracking()
            .Where(h => h.EntityType == EntityTypes.Branch && ids.Contains(h.EntityId) && h.StartedAt < end && (h.EndedAt == null || h.EndedAt > start))
            .ToListAsync(ct);
        var maint = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.StartsAt < end && w.EndsAt > start && w.DeviceId == null
                        && (w.ApprovedBy != null || w.EndsAt - w.StartsAt <= TimeSpan.FromHours(24)))
            .ToListAsync(ct);
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.BranchId) && i.DetectedAt >= start && i.DetectedAt < end).ToListAsync(ct);

        var rows = new List<object?[]>();
        var flagged = new List<int>();
        foreach (var b in branches)
        {
            var (pct, down) = AvailabilityCalculator.Compute(start, end,
                history.Where(h => h.EntityId == b.BranchId).Select(h => new StatusInterval(h.Status, h.StartedAt, h.EndedAt)),
                maint.Where(m => m.BranchId == b.BranchId).Select(m => new Window(m.StartsAt, m.EndsAt)));
            var inc = incidents.Where(i => i.BranchId == b.BranchId).ToList();
            var resolved = inc.Where(i => i.ResolvedAt is not null).ToList();
            var mttr = resolved.Count > 0 ? TimeSpan.FromSeconds(resolved.Average(i => (i.ResolvedAt!.Value - i.DetectedAt).TotalSeconds)) : (TimeSpan?)null;
            var cause = inc.Where(i => i.RootCause is not null).GroupBy(i => i.RootCause).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (pct < (double)b.SlaTargetPct) flagged.Add(rows.Count);
            rows.Add([b.BranchCode, b.BranchName, b.Region!.Name, Math.Round(pct, 2), (double)b.SlaTargetPct,
                      pct >= (double)b.SlaTargetPct ? "Met" : "Below", Fmt(down), inc.Count, mttr is { } m ? Fmt(m) : "—", cause ?? "—"]);
        }
        return new ReportTable($"Monthly Availability — {new DateTime(year, month, 1):MMMM yyyy}",
            $"SLA target per branch · maintenance excluded · generated {Local(DateTimeOffset.UtcNow):yyyy-MM-dd HH:mm}",
            ["Code", "Branch", "Region", "Availability %", "Target %", "SLA", "Downtime", "Incidents", "MTTR", "Main cause"], rows)
        { Flagged = flagged };
    }

    /// <summary>FR-RP-02.</summary>
    public async Task<ReportTable> IncidentsAsync(DateTimeOffset from, DateTimeOffset to, HashSet<int>? allowed, int? regionId, int? branchId, CancellationToken ct)
    {
        var branches = (await BranchesAsync(allowed, regionId, branchId, ct)).ToDictionary(b => b.BranchId);
        var ids = branches.Keys.ToList();
        var list = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.BranchId) && i.DetectedAt >= from && i.DetectedAt < to)
            .OrderBy(i => i.DetectedAt).ToListAsync(ct);
        var rows = list.Select(i => new object?[]
        {
            i.IncidentNo, branches[i.BranchId].BranchCode, i.Severity, i.Status, i.RootCause ?? "—", i.Title,
            Local(i.DetectedAt).ToString("yyyy-MM-dd HH:mm"),
            i.AckAt is { } a ? Fmt(a - i.DetectedAt) : "—",
            i.ResolvedAt is { } r ? Fmt(r - i.DetectedAt) : "—",
        }).ToList();
        return new ReportTable($"Incident Report — {Local(from):yyyy-MM-dd} to {Local(to).AddSeconds(-1):yyyy-MM-dd}",
            $"{list.Count} incidents · by branch, root cause and MTTR",
            ["No.", "Branch", "Severity", "Status", "Root cause", "Title", "Detected", "MTTA", "MTTR"], rows);
    }

    /// <summary>FR-RP-03 (P2): availability proxy, latency and loss per ISP for the month.</summary>
    public async Task<ReportTable> IspAsync(int year, int month, HashSet<int>? allowed, CancellationToken ct)
    {
        var (start, end) = MonthRange(year, month);
        await using var cmd = ds.CreateCommand("""
            SELECT i.name,
                   count(DISTINCT l.isp_link_id) AS links,
                   avg(h.avg_value) FILTER (WHERE h.metric_key = 'latency_ms') AS latency,
                   avg(h.avg_value) FILTER (WHERE h.metric_key = 'loss_pct')   AS loss,
                   avg(h.avg_value) FILTER (WHERE h.metric_key = 'up') * 100  AS up_pct
            FROM org.isp i
            JOIN org.isp_link l ON l.isp_id = i.isp_id
            LEFT JOIN mon.metric_hourly h ON h.entity_type = 'ISPLINK' AND h.entity_id = l.isp_link_id
                                         AND h.bucket >= $1 AND h.bucket < $2
            WHERE $3::int[] IS NULL OR l.branch_id = ANY($3)
            GROUP BY i.name ORDER BY i.name
            """);
        cmd.Parameters.AddWithValue(start);
        cmd.Parameters.AddWithValue(end);
        cmd.Parameters.Add(new NpgsqlParameter { Value = allowed is null ? DBNull.Value : allowed.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer });
        var ispIncidents = await (from inc in db.Incidents
                                  where inc.RootCause == "ISP" && inc.DetectedAt >= start && inc.DetectedAt < end
                                  select inc.BranchId).ToListAsync(ct);
        var rows = new List<object?[]>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            double? D(int i) => r.IsDBNull(i) ? null : Math.Round(r.GetDouble(i), 2);
            rows.Add([r.GetString(0), r.GetInt64(1), D(4), D(2), D(3)]);
        }
        return new ReportTable($"ISP Comparison — {new DateTime(year, month, 1):MMMM yyyy}",
            $"Hourly averages from branch agents · {ispIncidents.Count} incidents with root cause ISP this month",
            ["ISP", "Links", "Up %", "Avg latency ms", "Avg loss %"], rows);
    }

    /// <summary>FR-RP-04 (P2): power outages and time on UPS battery per branch.</summary>
    public async Task<ReportTable> PowerAsync(int year, int month, HashSet<int>? allowed, int? regionId, CancellationToken ct)
    {
        var (start, end) = MonthRange(year, month);
        var branches = await BranchesAsync(allowed, regionId, null, ct);
        var ids = branches.Select(b => b.BranchId).ToArray();
        var onBattery = new Dictionary<int, double>();
        await using (var cmd = ds.CreateCommand("""
            SELECT d.branch_id, sum(h.avg_value * 60) AS minutes
            FROM mon.metric_hourly h JOIN inv.device d ON h.entity_type = 'DEVICE' AND d.device_id = h.entity_id
            WHERE h.metric_key = 'ups_on_battery' AND h.bucket >= $1 AND h.bucket < $2 AND d.branch_id = ANY($3)
            GROUP BY d.branch_id
            """))
        {
            cmd.Parameters.AddWithValue(start); cmd.Parameters.AddWithValue(end); cmd.Parameters.AddWithValue(ids);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) onBattery[r.GetInt32(0)] = r.IsDBNull(1) ? 0 : r.GetDouble(1);
        }
        var powerOff = await db.StatusHistory.AsNoTracking()
            .Where(h => h.EntityType == EntityTypes.Branch && h.Status == BranchStatus.OfflinePower && h.StartedAt >= start && h.StartedAt < end)
            .GroupBy(h => h.EntityId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var powerIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.RootCause == "POWER" && i.DetectedAt >= start && i.DetectedAt < end)
            .GroupBy(i => i.BranchId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = branches.Select(b => new object?[]
        {
            b.BranchCode, b.BranchName, b.Region!.Name,
            Fmt(TimeSpan.FromMinutes(onBattery.GetValueOrDefault(b.BranchId))),
            powerOff.GetValueOrDefault(b.BranchId), powerIncidents.GetValueOrDefault(b.BranchId),
        }).ToList();
        return new ReportTable($"Power Report — {new DateTime(year, month, 1):MMMM yyyy}", "Time on UPS battery and full power outages",
            ["Code", "Branch", "Region", "On battery", "Power outages", "Power incidents"], rows);
    }

    private DateTimeOffset Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Tz);

    public static string Fmt(TimeSpan t) =>
        t.TotalMinutes < 1 ? "0 m" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} m" : $"{t.Minutes} m";
}
