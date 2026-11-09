using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Status;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Incidents;

public sealed record IncidentPatch(string? Status, int? AssignedUserId, string? RootCause, string? ResolutionNote);
public sealed record NoteRequest(string Text);
public sealed record TaskCallback(string TicketId, string? Status, string? Assignee, string? RootCause, string? Note);

/// <summary>SCR-06, FR-IN-03/04/06, INT-01 callback.</summary>
public static class IncidentEndpoints
{
    public static void MapIncidentApi(this IEndpointRouteBuilder api)
    {
        api.MapGet("/incidents", List).RequireAuthorization(AuthPolicies.Viewer);
        api.MapGet("/incidents/{id:long}", Get).RequireAuthorization(AuthPolicies.Viewer);
        api.MapPatch("/incidents/{id:long}", Patch).RequireAuthorization(AuthPolicies.Ack);
        api.MapPost("/incidents/{id:long}/notes", AddNote).RequireAuthorization(AuthPolicies.Ack);
        api.MapGet("/users/assignable", Assignable).RequireAuthorization(AuthPolicies.Ack);
        api.MapPost("/integrations/taskmgmt/callback", Callback).AllowAnonymous();
    }

    /// <param name="status">open (default) | resolved | all</param>
    private static async Task<IResult> List(HttpContext http, NmsDb db, BranchScope scope, string? status, int? branchId,
        bool? mine, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var q = db.Incidents.AsNoTracking().Where(i => allowed == null || allowed.Contains(i.BranchId));
        q = status switch
        {
            "all" => q,
            "resolved" => q.Where(i => i.Status == IncidentStatus.Resolved || i.Status == IncidentStatus.Closed),
            _ => q.Where(i => IncidentStatus.Active.Contains(i.Status)),
        };
        if (branchId is { } b) q = q.Where(i => i.BranchId == b);
        if (mine == true) { var uid = http.User.UserId(); q = q.Where(i => i.AssignedUserId == uid); }
        if (from?.ToUniversalTime() is { } f) q = q.Where(i => i.DetectedAt >= f);
        if (to?.ToUniversalTime() is { } t) q = q.Where(i => i.DetectedAt < t);

        var size = Math.Clamp(pageSize ?? 50, 1, 500);
        var pg = Math.Max(page ?? 1, 1);
        var total = await q.CountAsync(ct);
        var items = await (from i in q
                           join br in db.Branches on i.BranchId equals br.BranchId
                           join u in db.Users on i.AssignedUserId equals u.UserId into uj
                           from u in uj.DefaultIfEmpty()
                           orderby i.DetectedAt descending
                           select new
                           {
                               i.IncidentId, i.IncidentNo, i.BranchId, BranchCode = br.BranchCode, BranchName = br.BranchName,
                               i.Severity, i.Status, i.Title, Assignee = u != null ? u.DisplayName : null, i.AssignedUserId,
                               i.DetectedAt, i.AckAt, i.ResolvedAt, i.EscalationLevel, i.TaskTicketId, i.TicketStatus, i.RootCause,
                           }).Skip((pg - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { items, page = pg, pageSize = size, total });
    }

    private static async Task<IResult> Get(long id, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        var i = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(x => x.IncidentId == id, ct);
        if (i is null || !await scope.CanSeeAsync(http.User, i.BranchId, ct)) return Results.NotFound();
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.BranchId == i.BranchId, ct);
        var assignee = i.AssignedUserId is { } uid ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == uid, ct) : null;
        var alerts = await (from x in db.IncidentAlerts
                            join a in db.Alerts on x.AlertId equals a.AlertId
                            where x.IncidentId == id
                            orderby a.RaisedAt
                            select new { a.AlertId, a.Severity, a.Title, a.RaisedAt, a.ClearedAt, a.IsSuppressed, a.SuppressReason, a.AckAt })
            .ToListAsync(ct);
        var timeline = await (from t in db.IncidentTimeline
                              join u in db.Users on t.UserId equals u.UserId into uj
                              from u in uj.DefaultIfEmpty()
                              where t.IncidentId == id
                              orderby t.At, t.TimelineId
                              select new { t.At, t.Kind, t.Text, By = u != null ? u.DisplayName : "System" }).ToListAsync(ct);
        return Results.Ok(new
        {
            i.IncidentId, i.IncidentNo, i.BranchId, BranchCode = branch.BranchCode, BranchName = branch.BranchName,
            i.Severity, i.Status, i.Title, i.RootCause, i.ResolutionNote, i.AssignedUserId, Assignee = assignee?.DisplayName,
            i.EscalationLevel, i.TaskTicketId, i.TicketStatus, i.StartedAt, i.DetectedAt, i.AckAt, i.ResolvedAt, i.ClosedAt,
            Metrics = IncidentMetrics.For(i),
            Alerts = alerts, Timeline = timeline,
        });
    }

    private static async Task<IResult> Patch(long id, IncidentPatch req, HttpContext http, NmsDb db, BranchScope scope,
        IHubContext<StatusHub> hub, TaskManagementClient tasks, CancellationToken ct)
    {
        var i = await db.Incidents.FirstOrDefaultAsync(x => x.IncidentId == id, ct);
        if (i is null || !await scope.CanSeeAsync(http.User, i.BranchId, ct)) return Results.NotFound();
        var userId = http.User.UserId();
        var now = DateTimeOffset.UtcNow;
        var before = new { i.Status, i.AssignedUserId, i.RootCause, i.ResolutionNote };

        if (req.RootCause is { } rc)
        {
            if (!RootCauses.All.Contains(rc)) return Invalid("rootCause", $"Must be one of {string.Join(", ", RootCauses.All)}");
            i.RootCause = rc;
        }
        if (req.ResolutionNote is { } note) i.ResolutionNote = note.Trim();

        if (req.AssignedUserId is { } assignTo && assignTo != i.AssignedUserId)
        {
            var target = await db.Users.FirstOrDefaultAsync(u => u.UserId == assignTo && u.IsActive, ct);
            if (target is null) return Invalid("assignedUserId", "Unknown or inactive user");
            i.AssignedUserId = assignTo;
            IncidentEngine.Timeline(db, i, "ASSIGN", $"Assigned to {target.DisplayName}", userId);
        }

        if (req.Status is { } status && status != i.Status)
        {
            if (IncidentTransitions.Check(i.Status, status) is { } error) return Invalid("status", error);
            if (status == IncidentStatus.Closed && !http.User.IsInRole(Roles.SuperAdmin) && !http.User.IsInRole(Roles.HeadOfIt)
                && !http.User.IsInRole(Roles.ItManager))
                return Results.Problem("Only IT Manager or above can close incidents", statusCode: 403);
            if (status == IncidentStatus.Resolved && (i.RootCause is null || string.IsNullOrWhiteSpace(i.ResolutionNote)))
                return Invalid("rootCause", "Root cause and resolution note are required to resolve (FR-IN-04)");

            if (i.AckAt is null && status != IncidentStatus.Open)
            {
                i.AckAt = now;
                // Acknowledging the incident stops repeat notifications for its alerts (FR-AL-08).
                var alertIds = db.IncidentAlerts.Where(x => x.IncidentId == id).Select(x => x.AlertId);
                await db.Alerts.Where(a => alertIds.Contains(a.AlertId) && a.AckAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.AckAt, now).SetProperty(a => a.AckByUserId, userId)
                        .SetProperty(a => a.AckComment, "Acknowledged via incident " + i.IncidentNo), ct);
            }
            if (status == IncidentStatus.Resolved) i.ResolvedAt = now;
            if (status == IncidentStatus.Closed) i.ClosedAt = now;
            if (status is IncidentStatus.InProgress or IncidentStatus.Ack && i.ResolvedAt is not null) i.ResolvedAt = null;   // reopened
            IncidentEngine.Timeline(db, i, "STATUS", $"Status {i.Status} → {status}", userId);
            i.Status = status;
        }

        if (tasks.Enabled && i.TaskTicketId is not null) { i.TicketNextAttemptAt = now; i.TicketAttempts = 0; }
        db.Record(http, "INCIDENT_UPDATE", "incident", i.IncidentNo, before, req);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem("The incident was changed by someone else. Reload and try again.", statusCode: 409);
        }
        await hub.SendForBranchAsync(i.BranchId, "incidentUpdated", new { incidentId = i.IncidentId, i.Status, assignee = i.AssignedUserId }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddNote(long id, NoteRequest req, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return Invalid("text", "Note text is required");
        var i = await db.Incidents.FirstOrDefaultAsync(x => x.IncidentId == id, ct);
        if (i is null || !await scope.CanSeeAsync(http.User, i.BranchId, ct)) return Results.NotFound();
        IncidentEngine.Timeline(db, i, "NOTE", req.Text.Trim(), http.User.UserId());
        db.Record(http, "INCIDENT_NOTE", "incident", i.IncidentNo);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Assignable(NmsDb db, CancellationToken ct) =>
        Results.Ok(await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Role != Roles.Management)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.UserId, u.DisplayName, u.Role }).ToListAsync(ct));

    /// <summary>Task Management → CBH-NMS (INT-01): API key + IP allowlist.</summary>
    private static async Task<IResult> Callback(TaskCallback cb, HttpContext http, NmsDb db, IOptions<TaskManagementOptions> opt,
        IHubContext<StatusHub> hub, CancellationToken ct)
    {
        var o = opt.Value;
        if (string.IsNullOrEmpty(o.CallbackApiKey)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(http.Request.Headers["X-Api-Key"].ToString()),
                System.Text.Encoding.UTF8.GetBytes(o.CallbackApiKey)))
            return Results.Unauthorized();
        var ip = http.Connection.RemoteIpAddress;
        if (o.CallbackAllowedIps.Count > 0 && (ip is null || !o.CallbackAllowedIps.Contains(ip.MapToIPv4().ToString())))
            return Results.Forbid();

        var i = await db.Incidents.FirstOrDefaultAsync(x => x.TaskTicketId == cb.TicketId, ct);
        if (i is null) return Results.NotFound();

        if (cb.Assignee is { Length: > 0 } who)
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.UserName == who || x.Email == who, ct);
            if (u is not null && u.UserId != i.AssignedUserId)
            {
                i.AssignedUserId = u.UserId;
                IncidentEngine.Timeline(db, i, "ASSIGN", $"Assigned to {u.DisplayName} (from Task Management)");
            }
        }
        if (cb.RootCause is { } rc && RootCauses.All.Contains(rc)) i.RootCause = rc;
        if (cb.Note is { Length: > 0 } note) IncidentEngine.Timeline(db, i, "NOTE", $"[Task Management] {note}");

        if (MapStatus(cb.Status) is { } status && status != i.Status && IncidentTransitions.Check(i.Status, status) is null)
        {
            if (status is IncidentStatus.Resolved or IncidentStatus.Closed && i.RootCause is null)
                IncidentEngine.Timeline(db, i, "TICKET", "Ticket resolved in Task Management — set a root cause here to resolve the incident");
            else
            {
                var now = DateTimeOffset.UtcNow;
                if (i.AckAt is null) i.AckAt = now;
                if (status == IncidentStatus.Resolved) { i.ResolvedAt = now; i.ResolutionNote ??= cb.Note ?? "Resolved in Task Management"; }
                if (status == IncidentStatus.Closed) i.ClosedAt = now;
                IncidentEngine.Timeline(db, i, "STATUS", $"Status {i.Status} → {status} (from Task Management)");
                i.Status = status;
            }
        }
        db.AuditLog.Add(new AuditLog { UserName = "taskmgmt", ClientIp = ip, Action = "TASKMGMT_CALLBACK", EntityType = "incident", EntityId = i.IncidentNo });
        await db.SaveChangesAsync(ct);
        await hub.SendForBranchAsync(i.BranchId, "incidentUpdated", new { incidentId = i.IncidentId, i.Status, assignee = i.AssignedUserId }, ct);
        return Results.NoContent();
    }

    private static string? MapStatus(string? s) => s?.Trim().ToUpperInvariant().Replace(" ", "_") switch
    {
        "OPEN" or "NEW" => IncidentStatus.Open,
        "ACK" or "ACKNOWLEDGED" or "ASSIGNED" => IncidentStatus.Ack,
        "IN_PROGRESS" or "INPROGRESS" or "WORKING" => IncidentStatus.InProgress,
        "RESOLVED" or "DONE" or "FIXED" => IncidentStatus.Resolved,
        "CLOSED" => IncidentStatus.Closed,
        _ => null,
    };

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>FR-IN-03: Open → Acknowledged → In Progress → Resolved → Closed. Forward skips allowed;
/// a resolved incident can be reopened; closed is final.</summary>
public static class IncidentTransitions
{
    public static string? Check(string from, string to)
    {
        if (!IncidentStatus.All.Contains(to)) return $"Unknown status '{to}'";
        if (from == IncidentStatus.Closed) return "Closed incidents cannot change";
        var order = Array.IndexOf(IncidentStatus.All, to) - Array.IndexOf(IncidentStatus.All, from);
        if (order > 0) return null;
        if (from == IncidentStatus.Resolved && to is IncidentStatus.InProgress) return null;   // reopen
        return $"Cannot move from {from} to {to}";
    }
}

/// <summary>FR-IN-06. MTTD = start → detection; MTTA = detection → ack; MTTR = detection → resolution.</summary>
public static class IncidentMetrics
{
    public static object For(Incident i) => new
    {
        mttdSeconds = (int)(i.DetectedAt - i.StartedAt).TotalSeconds,
        mttaSeconds = i.AckAt is { } a ? (int?)(a - i.DetectedAt).TotalSeconds : null,
        mttrSeconds = i.ResolvedAt is { } r ? (int?)(r - i.DetectedAt).TotalSeconds : null,
    };
}
