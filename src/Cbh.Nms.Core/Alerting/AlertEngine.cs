using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Endpoints;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Core.Status;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cbh.Nms.Core.Alerting;

/// <summary>
/// Alert half of the Evaluation Engine (FR-AL-01..05). Every 15 s: evaluates threshold rules against
/// recent metrics, turns branch Offline/Unknown status into alerts, raises/clears ops.alert rows
/// (one active row per dedup key), applies suppression and flapping control, and hands new
/// alerts to the notification planner.
/// </summary>
public sealed class AlertEngine(IServiceScopeFactory scopes, NpgsqlDataSource ds, IHubContext<StatusHub> hub,
    EndpointAlertSource endpoints, ILogger<AlertEngine> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);
    public const int FlapThreshold = 3;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);   // let the status engine run first
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Alert evaluation failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private sealed record Candidate(string DedupKey, int? RuleId, int BranchId, int? DeviceId, string? EntityType,
        int? EntityId, string Severity, string Title, double? Value, bool IsStatusAlert);

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDb>();
        var planner = scope.ServiceProvider.GetRequiredService<NotificationPlanner>();
        var now = DateTimeOffset.UtcNow;

        var labels = await EntityLabels.LoadAsync(db, ct);
        var firing = new Dictionary<string, Candidate>();
        var normal = new HashSet<string>();

        // ── threshold rules ──
        var rules = await db.AlertRules.AsNoTracking().Where(r => r.IsEnabled).ToListAsync(ct);
        foreach (var rule in rules)
        {
            foreach (var (entityId, branchId, samples) in await LoadSamplesAsync(rule, now, ct))
            {
                var key = $"r{rule.AlertRuleId}:{rule.EntityType}:{entityId}:b{branchId}";
                var (state, value) = RuleEvaluator.Evaluate(samples, rule.Operator, rule.Threshold, rule.ForSeconds, now);
                if (state == RuleState.Normal) normal.Add(key);
                else if (state == RuleState.Firing)
                    firing[key] = new Candidate(key, rule.AlertRuleId, branchId,
                        rule.EntityType == EntityTypes.Device ? entityId : null, rule.EntityType, entityId, rule.Severity,
                        $"{rule.Name} · {labels.Describe(rule.EntityType, entityId, branchId)}", value, false);
            }
        }

        // ── branch status → alerts (§9.1, §9.2: Tier-1 offline is Critical) ──
        var statuses = await db.StatusCurrent.AsNoTracking().Where(s => s.EntityType == EntityTypes.Branch).ToListAsync(ct);
        foreach (var s in statuses)
        {
            var (severity, title) = s.Status switch
            {
                BranchStatus.OfflinePower => (labels.Tier(s.EntityId) == 1 ? Severity.Critical : Severity.High, "Branch offline · Power"),
                BranchStatus.OfflineIsp => (labels.Tier(s.EntityId) == 1 ? Severity.Critical : Severity.High, "Branch offline · ISP/MPLS"),
                BranchStatus.Unknown => (Severity.Warning, "Monitoring lost · agent heartbeat missing"),
                _ => (null, null),
            };
            if (severity is null) continue;
            var key = $"status:{s.EntityId}:{s.Status}";
            firing[key] = new Candidate(key, null, s.EntityId, null, EntityTypes.Branch, s.EntityId, severity,
                $"{title!} · {labels.Branch(s.EntityId)}", null, true);
        }

        // ── HO path (MPLS / VPN) down while the branch may still be up on the other path (D-13) ──
        // Not a branch-status alert, so a whole-branch outage suppresses it as PARENT_DOWN.
        foreach (var s in statuses)
        {
            foreach (var path in HoPathsDown(s.Detail))
            {
                var key = $"hopath:{s.EntityId}:{path}";
                var severity = labels.Tier(s.EntityId) == 1 ? Severity.High : Severity.Warning;
                firing[key] = new Candidate(key, null, s.EntityId, null, EntityTypes.Branch, s.EntityId, severity,
                    $"{path} path to HO down · {labels.Branch(s.EntityId)}", null, false);
            }
        }

        // ── user PCs (D-14) ──
        foreach (var f in await endpoints.GetAsync(now, ct))
            firing[f.DedupKey] = new Candidate(f.DedupKey, null, f.BranchId, null, EntityTypes.Endpoint, f.EndpointId,
                f.Severity, f.Title, null, false);

        var ctx = await LoadSuppressionContextAsync(db, statuses, now, ct);
        var active = await db.Alerts.Where(a => a.ClearedAt == null).ToListAsync(ct);
        var activeKeys = active.ToDictionary(a => a.DedupKey);
        var raised = new List<Alert>();

        // ── clear ──
        var ruleIds = rules.Select(r => r.AlertRuleId).ToHashSet();
        foreach (var a in active)
        {
            var isStatus = a.DedupKey.StartsWith("status:", StringComparison.Ordinal)
                           || a.DedupKey.StartsWith("hopath:", StringComparison.Ordinal)
                           || a.DedupKey.StartsWith("ep:", StringComparison.Ordinal);
            var isFlap = a.DedupKey.StartsWith("flap:", StringComparison.Ordinal);
            var clear =
                (isStatus && !firing.ContainsKey(a.DedupKey))
                || (a.AlertRuleId is { } rid && !ruleIds.Contains(rid))      // rule disabled or deleted
                || normal.Contains(a.DedupKey)
                || (isFlap && await FlapSettledAsync(db, a.DedupKey["flap:".Length..], now, activeKeys, ct));
            if (!clear) continue;
            a.ClearedAt = now;
            await hub.SendForBranchAsync(a.BranchId, "alertCleared",
                new { alertId = a.AlertId, a.BranchId, a.Severity, a.Title }, ct);
            if (!a.IsSuppressed) planner.AlertCleared(a);
        }

        // ── re-check suppression of still-active alerts (e.g. parent recovered, child still down) ──
        foreach (var a in active.Where(a => a.ClearedAt == null && a.IsSuppressed && a.SuppressReason != Suppression.Flapping))
        {
            var reason = Suppression.Decide(a.BranchId, a.EntityType, a.DeviceId, a.DedupKey.StartsWith("status:"), ctx);
            if (reason is null)
            {
                a.IsSuppressed = false;
                a.SuppressReason = null;
                raised.Add(a);
            }
            else a.SuppressReason = reason;
        }

        // ── raise ──
        foreach (var c in firing.Values)
        {
            if (activeKeys.TryGetValue(c.DedupKey, out var existing))
            {
                existing.LastValue = c.Value;
                continue;
            }
            var alert = new Alert
            {
                DedupKey = c.DedupKey, AlertRuleId = c.RuleId, BranchId = c.BranchId, DeviceId = c.DeviceId,
                EntityType = c.EntityType, EntityId = c.EntityId, Severity = c.Severity, Title = c.Title,
                LastValue = c.Value, RaisedAt = now,
            };
            alert.SuppressReason = Suppression.Decide(c.BranchId, c.EntityType, c.DeviceId, c.IsStatusAlert, ctx);
            if (alert.SuppressReason is null && await IsFlappingAsync(db, c.DedupKey, now, ct))
            {
                alert.SuppressReason = Suppression.Flapping;
                var flapKey = "flap:" + c.DedupKey;
                if (!activeKeys.ContainsKey(flapKey))
                {
                    var flap = new Alert
                    {
                        DedupKey = flapKey, BranchId = c.BranchId, DeviceId = c.DeviceId, EntityType = c.EntityType,
                        EntityId = c.EntityId, Severity = c.Severity, Title = "Unstable (flapping) · " + c.Title, RaisedAt = now,
                    };
                    db.Alerts.Add(flap);
                    activeKeys[flapKey] = flap;
                    raised.Add(flap);
                }
            }
            alert.IsSuppressed = alert.SuppressReason is not null;
            db.Alerts.Add(alert);
            activeKeys[c.DedupKey] = alert;
            if (!alert.IsSuppressed) raised.Add(alert);
        }

        await db.SaveChangesAsync(ct);

        foreach (var a in raised)
        {
            planner.AlertRaised(a);
            await hub.SendForBranchAsync(a.BranchId, "alertRaised",
                new { alertId = a.AlertId, a.BranchId, a.Severity, a.Title, a.RaisedAt }, ct);
        }
        if (raised.Count > 0) log.LogInformation("Raised {Count} alert(s)", raised.Count);

        await planner.RepeatUnackedAsync(now, ct);   // FR-AL-08: repeat until acknowledged
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<(int EntityId, int BranchId, List<Sample> Samples)>> LoadSamplesAsync(AlertRule rule,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT m.entity_id, a.branch_id, m.time, m.value
            FROM mon.metric_raw m JOIN inv.agent a ON a.agent_id = m.agent_id
            WHERE m.entity_type = $1 AND m.metric_key = $2 AND m.time > $3
              AND ($4::int IS NULL OR a.branch_id = $4)
            """);
        cmd.Parameters.AddWithValue(rule.EntityType);
        cmd.Parameters.AddWithValue(rule.MetricKey);
        cmd.Parameters.AddWithValue(now - TimeSpan.FromSeconds(rule.ForSeconds) - RuleEvaluator.StaleAfter - TimeSpan.FromMinutes(1));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)rule.ScopeBranchId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        var groups = new Dictionary<(int, int), List<Sample>>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var k = (r.GetInt32(0), r.GetInt32(1));
            if (!groups.TryGetValue(k, out var list)) groups[k] = list = [];
            list.Add(new Sample(r.GetFieldValue<DateTimeOffset>(2), r.GetDouble(3)));
        }
        return groups.Select(g => (g.Key.Item1, g.Key.Item2, g.Value)).ToList();
    }

    private async Task<SuppressionContext> LoadSuppressionContextAsync(NmsDb db, List<StatusCurrent> statuses,
        DateTimeOffset now, CancellationToken ct)
    {
        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.StartsAt <= now && w.EndsAt > now
                        && (w.ApprovedBy != null || w.EndsAt - w.StartsAt <= TimeSpan.FromHours(24)))
            .ToListAsync(ct);

        var devicesDown = new HashSet<int>();
        await using (var cmd = ds.CreateCommand("""
            SELECT DISTINCT ON (entity_id) entity_id, value FROM mon.metric_raw
            WHERE entity_type = 'DEVICE' AND metric_key = 'up' AND time > $1
            ORDER BY entity_id, time DESC
            """))
        {
            cmd.Parameters.AddWithValue(now - RuleEvaluator.StaleAfter);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (r.GetDouble(1) == 0) devicesDown.Add(r.GetInt32(0));
        }

        var edges = await db.DeviceDependencies.AsNoTracking().ToListAsync(ct);
        var parentsOf = edges.GroupBy(e => e.ChildDeviceId).ToDictionary(g => g.Key, g => g.Select(e => e.ParentDeviceId).ToList());
        var routers = await db.Devices.AsNoTracking()
            .Where(d => d.IsActive && (d.DeviceTypeId == 1 || d.DeviceTypeId == 2))
            .Select(d => new { d.DeviceId, d.BranchId }).ToListAsync(ct);

        return new SuppressionContext
        {
            BranchStatus = statuses.ToDictionary(s => s.EntityId, s => s.Status),
            BranchesInMaintenance = windows.Where(w => w.BranchId != null && w.DeviceId == null).Select(w => w.BranchId!.Value).ToHashSet(),
            DevicesInMaintenance = windows.Where(w => w.DeviceId != null).Select(w => w.DeviceId!.Value).ToHashSet(),
            DevicesDown = devicesDown,
            ParentsOf = parentsOf,
            RootDevicesOf = routers.Where(r => !parentsOf.ContainsKey(r.DeviceId))
                .GroupBy(r => r.BranchId).ToDictionary(g => g.Key, g => g.Select(r => r.DeviceId).ToList()),
        };
    }

    /// <summary>FR-AL-04: more than 3 state changes in 10 minutes ⇒ flapping.</summary>
    private static async Task<bool> IsFlappingAsync(NmsDb db, string dedupKey, DateTimeOffset now, CancellationToken ct) =>
        await db.Alerts.CountAsync(a => a.DedupKey == dedupKey && a.RaisedAt > now - FlapWindow, ct) >= FlapThreshold;

    private static async Task<bool> FlapSettledAsync(NmsDb db, string dedupKey, DateTimeOffset now,
        Dictionary<string, Alert> activeKeys, CancellationToken ct) =>
        !activeKeys.ContainsKey(dedupKey)
        && !await db.Alerts.AnyAsync(a => a.DedupKey == dedupKey && a.RaisedAt > now - FlapWindow, ct);

    private static IEnumerable<string> HoPathsDown(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return [];
        using var doc = System.Text.Json.JsonDocument.Parse(detail);
        return doc.RootElement.TryGetProperty("hoPathsDown", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
            : [];
    }
}
