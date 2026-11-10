using System.Text.Json;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Cbh.Nms.Core.Status;

/// <summary>
/// Branch half of the Evaluation Engine: every 15 s gathers §9.1 signals for all active branches in
/// one query, applies <see cref="BranchStatusRules"/>, and records + broadcasts status changes.
/// Metric-threshold alert rules (FR-AL-01) are a separate pass, added with the alerting module.
/// </summary>
public sealed class StatusEngine(NpgsqlDataSource dataSource, IHubContext<StatusHub> hub, ILogger<StatusEngine> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Status evaluation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var signals = await LoadSignalsAsync(now, ct);
        foreach (var (branchId, s) in signals)
        {
            var verdict = BranchStatusRules.Evaluate(s);
            var change = await SaveAsync(branchId, verdict, now, ct);
            if (change is { } old)
                await hub.SendForBranchAsync(branchId, "branchStatusChanged",
                    new { branchId, oldStatus = old, newStatus = verdict.Status, reason = verdict.Reason, atUtc = now }, ct);
        }
    }

    private const string SignalsSql = """
        WITH b AS (
            SELECT branch_id, mpls_router_ip IS NOT NULL AS mpls, vpn_router_ip IS NOT NULL AS vpn,
                   loss_threshold_pct, latency_threshold_ms
            FROM org.branch WHERE is_active),
        hb AS (
            SELECT branch_id, max(last_seen_at) AS last_hb FROM inv.agent WHERE NOT is_revoked GROUP BY branch_id),
        fails AS (   -- per path: start of the current run of failed pings
            SELECT r.branch_id, r.path, min(r.time) AS failing_since
            FROM mon.reachability r
            WHERE r.time > $1 - interval '1 hour' AND NOT r.success
              AND r.time > coalesce((SELECT max(ok.time) FROM mon.reachability ok
                                     WHERE ok.branch_id = r.branch_id AND ok.path = r.path AND ok.success
                                       AND ok.time > $1 - interval '1 hour'), '-infinity')
            GROUP BY r.branch_id, r.path),
        ping AS (
            SELECT branch_id,
                   min(failing_since) FILTER (WHERE path = 'MPLS') AS mpls_failing,
                   min(failing_since) FILTER (WHERE path = 'VPN')  AS vpn_failing
            FROM fails GROUP BY branch_id),
        latest AS (
            SELECT DISTINCT ON (m.agent_id, m.entity_type, m.entity_id, m.metric_key)
                   a.branch_id, m.entity_type, m.entity_id, m.metric_key, m.value
            FROM mon.metric_raw m JOIN inv.agent a ON a.agent_id = m.agent_id
            WHERE m.time > $1 - interval '30 minutes'
              AND m.metric_key IN ('up', 'ups_on_battery', 'ups_battery_pct')
            ORDER BY m.agent_id, m.entity_type, m.entity_id, m.metric_key, m.time DESC),
        agg AS (
            SELECT l.branch_id,
                   max(l.value) FILTER (WHERE l.metric_key = 'ups_on_battery')               AS ups_on_battery,
                   min(l.value) FILTER (WHERE l.metric_key = 'ups_battery_pct')              AS ups_battery_pct,
                   count(*) FILTER (WHERE l.entity_type = 'ISPLINK' AND l.metric_key = 'up' AND l.value = 0) AS isp_down,
                   count(*) FILTER (WHERE l.entity_type = 'APP' AND l.metric_key = 'up' AND l.value = 0)     AS app_down,
                   count(*) FILTER (WHERE l.entity_type = 'DEVICE' AND l.metric_key = 'up' AND l.value = 0
                                     AND d.is_critical)                                        AS crit_down
            FROM latest l LEFT JOIN inv.device d ON l.entity_type = 'DEVICE' AND d.device_id = l.entity_id
            GROUP BY l.branch_id),
        wan AS (
            SELECT entity_id AS branch_id,
                   avg(value) FILTER (WHERE metric_key = 'loss_pct')   AS loss,
                   avg(value) FILTER (WHERE metric_key = 'latency_ms') AS latency
            FROM mon.metric_raw
            WHERE entity_type = 'BRANCH' AND time > $1 - interval '10 minutes'
              AND metric_key IN ('loss_pct', 'latency_ms')
            GROUP BY entity_id)
        SELECT b.branch_id, b.mpls, b.vpn, b.loss_threshold_pct, b.latency_threshold_ms, hb.last_hb,
               ping.mpls_failing, ping.vpn_failing,
               agg.ups_on_battery, agg.ups_battery_pct, coalesce(agg.isp_down, 0), coalesce(agg.app_down, 0),
               coalesce(agg.crit_down, 0), wan.loss, wan.latency
        FROM b LEFT JOIN hb USING (branch_id) LEFT JOIN ping USING (branch_id)
               LEFT JOIN agg USING (branch_id) LEFT JOIN wan USING (branch_id)
        """;

    private async Task<List<(int BranchId, BranchSignals Signals)>> LoadSignalsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(SignalsSql);
        cmd.Parameters.AddWithValue(now);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(int, BranchSignals)>();
        while (await r.ReadAsync(ct))
        {
            double? D(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
            DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
            var paths = new List<HoPathSignal>(2);
            if (r.GetBoolean(1)) paths.Add(new(HoPathNames.Mpls, T(6)));
            if (r.GetBoolean(2)) paths.Add(new(HoPathNames.Vpn, T(7)));
            list.Add((r.GetInt32(0), new BranchSignals
            {
                Now = now,
                HoPaths = paths,
                LossThresholdPct = Convert.ToDouble(r.GetValue(3)),
                LatencyThresholdMs = r.GetInt32(4),
                LastHeartbeat = T(5),
                UpsOnBattery = D(8) is { } ob ? ob >= 1 : null,
                UpsBatteryPct = D(9),
                IspLinksDown = (int)r.GetInt64(10),
                AppChecksFailing = (int)r.GetInt64(11),
                CriticalDevicesDown = (int)r.GetInt64(12),
                LossPct10m = D(13),
                LatencyMs10m = D(14),
            }));
        }
        return list;
    }

    /// <summary>Upserts status_current; on a change closes the open history row and opens a new one.
    /// Returns the previous status when it changed, otherwise null.</summary>
    private async Task<string?> SaveAsync(int branchId, BranchVerdict v, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string? old = null;
        await using (var sel = new NpgsqlCommand(
            "SELECT status FROM mon.status_current WHERE entity_type = 'BRANCH' AND entity_id = $1 FOR UPDATE", conn, tx))
        {
            sel.Parameters.AddWithValue(branchId);
            old = (string?)await sel.ExecuteScalarAsync(ct);
        }

        var detail = JsonSerializer.Serialize(new { reason = v.Reason, hoPathsDown = v.HoPathsDown });
        if (old == v.Status)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE mon.status_current SET detail = $2::jsonb, updated_at = $3 WHERE entity_type = 'BRANCH' AND entity_id = $1",
                conn, tx);
            upd.Parameters.AddWithValue(branchId); upd.Parameters.AddWithValue(detail); upd.Parameters.AddWithValue(now);
            await upd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return null;
        }

        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO mon.status_current (entity_type, entity_id, status, since, detail, updated_at)
            VALUES ('BRANCH', @id, @st, @now, @detail::jsonb, @now)
            ON CONFLICT (entity_type, entity_id) DO UPDATE
               SET status = excluded.status, since = excluded.since, detail = excluded.detail, updated_at = excluded.updated_at;
            UPDATE mon.status_history SET ended_at = @now
             WHERE entity_type = 'BRANCH' AND entity_id = @id AND ended_at IS NULL;
            INSERT INTO mon.status_history (entity_type, entity_id, status, started_at, in_maintenance)
            VALUES ('BRANCH', @id, @st, @now, EXISTS (SELECT 1 FROM mon.maintenance_window w
                                                 WHERE w.branch_id = @id AND @now BETWEEN w.starts_at AND w.ends_at));
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", branchId); cmd.Parameters.AddWithValue("st", v.Status);
            cmd.Parameters.AddWithValue("now", now); cmd.Parameters.AddWithValue("detail", detail);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        log.LogInformation("Branch {BranchId}: {Old} → {New} ({Reason})", branchId, old ?? "(none)", v.Status, v.Reason);
        return old ?? BranchStatus.Unknown;
    }
}
