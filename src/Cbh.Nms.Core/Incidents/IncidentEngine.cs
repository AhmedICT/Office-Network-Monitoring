using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Core.Status;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Incidents;

/// <summary>
/// FR-IN-01/02/05: opens incidents for Critical/High alerts active ≥ 3 min (grouping by branch),
/// escalates unacknowledged incidents per ops.escalation_policy, and syncs tickets to Task Management.
/// </summary>
public sealed class IncidentEngine(IServiceScopeFactory scopes, TaskManagementClient tasks, IHubContext<StatusHub> hub,
    TimeProvider clock, ILogger<IncidentEngine> log) : BackgroundService
{
    public static readonly TimeSpan OpenAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan[] TicketRetry = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Incident engine failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDb>();
        var planner = scope.ServiceProvider.GetRequiredService<NotificationPlanner>();
        var now = clock.GetUtcNow();

        await OpenIncidentsAsync(db, now, ct);
        await EscalateAsync(db, planner, now, ct);
        await NoteRecoveryAsync(db, now, ct);
        await db.SaveChangesAsync(ct);
        await SyncTicketsAsync(db, now, ct);
    }

    private async Task OpenIncidentsAsync(NmsDb db, DateTimeOffset now, CancellationToken ct)
    {
        var linked = db.IncidentAlerts.Select(x => x.AlertId);
        var candidates = await db.Alerts
            .Where(a => a.ClearedAt == null && !linked.Contains(a.AlertId))
            .OrderBy(a => a.RaisedAt).ToListAsync(ct);
        if (candidates.Count == 0) return;

        var open = await db.Incidents.Where(i => IncidentStatus.Active.Contains(i.Status)).ToListAsync(ct);
        var ruleFor = await db.AlertRules.AsNoTracking().ToDictionaryAsync(r => r.AlertRuleId, r => r.ForSeconds, ct);

        foreach (var a in candidates)
        {
            var incident = open.FirstOrDefault(i => i.BranchId == a.BranchId);
            var triggers = !a.IsSuppressed && a.Severity is Severity.Critical or Severity.High && now - a.RaisedAt >= OpenAfter;

            if (incident is null)
            {
                if (!triggers) continue;
                var seq = await db.Database.SqlQueryRaw<long>("SELECT nextval('ops.incident_no_seq') AS \"Value\"").SingleAsync(ct);
                var forSeconds = a.AlertRuleId is { } rid ? ruleFor.GetValueOrDefault(rid) : 90;
                incident = new Incident
                {
                    IncidentNo = $"INC-{now.Year}-{seq:000000}",
                    BranchId = a.BranchId, Severity = a.Severity, Status = IncidentStatus.Open, Title = a.Title,
                    StartedAt = a.RaisedAt.AddSeconds(-forSeconds), DetectedAt = a.RaisedAt,
                    TicketNextAttemptAt = tasks.Enabled ? now : null,
                };
                db.Incidents.Add(incident);
                await db.SaveChangesAsync(ct);
                open.Add(incident);
                Timeline(db, incident, "CREATED", $"Incident auto-created from alert: {a.Title}");
                log.LogInformation("Opened {No} for branch {Branch}: {Title}", incident.IncidentNo, a.BranchId, a.Title);
                await hub.SendForBranchAsync(a.BranchId, "incidentUpdated",
                    new { incidentId = incident.IncidentId, incident.IncidentNo, incident.Status, assignee = (int?)null }, ct);
            }
            else if (Rank(a.Severity) > Rank(incident.Severity) && !a.IsSuppressed)
            {
                Timeline(db, incident, "STATUS", $"Severity raised {incident.Severity} → {a.Severity}");
                incident.Severity = a.Severity;
            }

            // Related alerts of the same branch (including suppressed children) join the open incident.
            db.IncidentAlerts.Add(new IncidentAlert { IncidentId = incident.IncidentId, AlertId = a.AlertId });
            if (incident.DetectedAt != a.RaisedAt)
                Timeline(db, incident, "ALERT", $"Linked alert: {a.Title}{(a.IsSuppressed ? $" (suppressed: {a.SuppressReason})" : "")}");
        }
    }

    private async Task EscalateAsync(NmsDb db, NotificationPlanner planner, DateTimeOffset now, CancellationToken ct)
    {
        var policies = await db.EscalationPolicies.AsNoTracking().Where(p => p.Level >= 2).OrderBy(p => p.Level).ToListAsync(ct);
        var unacked = await db.Incidents.Where(i => i.Status == IncidentStatus.Open && i.AckAt == null).ToListAsync(ct);
        foreach (var i in unacked)
        {
            var next = policies.Where(p => p.Severity == i.Severity && p.Level > i.EscalationLevel
                                           && now - i.DetectedAt >= TimeSpan.FromMinutes(p.AfterMinutes))
                .MaxBy(p => p.Level);
            if (next is null) continue;
            i.EscalationLevel = next.Level;
            planner.Escalate(i, next);
            Timeline(db, i, "ESCALATED", $"Escalated to level {next.Level} ({next.TargetRole}) — not acknowledged in {next.AfterMinutes} min");
            if (tasks.Enabled && i.TaskTicketId is not null) i.TicketNextAttemptAt = now;
        }
    }

    /// <summary>Notes on the timeline when every linked alert has cleared; a human still resolves it with a root cause.</summary>
    private static async Task NoteRecoveryAsync(NmsDb db, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.Incidents.Where(i => IncidentStatus.Active.Contains(i.Status)).ToListAsync(ct);
        foreach (var i in active)
        {
            var alerts = await (from x in db.IncidentAlerts
                                join a in db.Alerts on x.AlertId equals a.AlertId
                                where x.IncidentId == i.IncidentId
                                select a.ClearedAt).ToListAsync(ct);
            if (alerts.Count == 0 || alerts.Any(c => c is null)) continue;
            var lastKind = await db.IncidentTimeline.Where(t => t.IncidentId == i.IncidentId && (t.Kind == "RECOVERED" || t.Kind == "ALERT" || t.Kind == "CREATED"))
                .OrderByDescending(t => t.At).ThenByDescending(t => t.TimelineId).Select(t => t.Kind).FirstOrDefaultAsync(ct);
            if (lastKind != "RECOVERED")
                Timeline(db, i, "RECOVERED", "All linked alerts have cleared — resolve with a root cause when confirmed");
        }
    }

    private async Task SyncTicketsAsync(NmsDb db, DateTimeOffset now, CancellationToken ct)
    {
        if (!tasks.Enabled) return;
        var due = await db.Incidents.Where(i => i.TicketNextAttemptAt != null && i.TicketNextAttemptAt <= now).ToListAsync(ct);
        foreach (var i in due)
        {
            var branchCode = await db.Branches.Where(b => b.BranchId == i.BranchId).Select(b => b.BranchCode).FirstAsync(ct);
            var assignee = i.AssignedUserId is { } uid
                ? await db.Users.Where(u => u.UserId == uid).Select(u => u.UserName).FirstOrDefaultAsync(ct) : null;
            try
            {
                if (i.TaskTicketId is null)
                {
                    i.TaskTicketId = await tasks.CreateTicketAsync(i, branchCode, assignee, ct);
                    Timeline(db, i, "TICKET", $"Ticket {i.TaskTicketId} created in Task Management");
                }
                else await tasks.UpdateTicketAsync(i, assignee, ct);
                i.TicketStatus = "SYNCED";
                i.TicketAttempts = 0;
                i.TicketNextAttemptAt = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                i.TicketAttempts++;
                if (i.TicketAttempts > TicketRetry.Length)
                {
                    i.TicketStatus = "FAILED";
                    i.TicketNextAttemptAt = null;
                    Timeline(db, i, "TICKET", $"Task Management sync failed after {TicketRetry.Length + 1} attempts: {ex.Message}");
                }
                else i.TicketNextAttemptAt = now + TicketRetry[i.TicketAttempts - 1];
                log.LogWarning("Ticket sync for {No} failed (attempt {N}): {Error}", i.IncidentNo, i.TicketAttempts, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public static void Timeline(NmsDb db, Incident i, string kind, string text, int? userId = null) =>
        db.IncidentTimeline.Add(new IncidentTimeline { IncidentId = i.IncidentId, Kind = kind, Text = text, UserId = userId });

    public static int Rank(string s) => s switch
    {
        Severity.Critical => 4, Severity.High => 3, Severity.Warning => 2, _ => 1,
    };
}
