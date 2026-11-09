using Cbh.Nms.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Alerting;

public sealed record AckRequest(string Comment);
public sealed record BulkAckRequest(long[] Ids, string Comment);

/// <summary>SCR-05 Alert Console, FR-AL-08.</summary>
public static class AlertEndpoints
{
    public static void MapAlertApi(this IEndpointRouteBuilder api)
    {
        api.MapGet("/alerts", List).RequireAuthorization(AuthPolicies.Viewer);
        api.MapPost("/alerts/{id:long}/ack", (long id, AckRequest req, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct) =>
            AckAsync([id], req.Comment, http, db, scope, ct)).RequireAuthorization(AuthPolicies.Ack);
        api.MapPost("/alerts/ack", (BulkAckRequest req, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct) =>
            AckAsync(req.Ids, req.Comment, http, db, scope, ct)).RequireAuthorization(AuthPolicies.Ack);
    }

    /// <param name="status">active (default) | recent (last 24 h incl. cleared)</param>
    private static async Task<IResult> List(HttpContext http, NmsDb db, BranchScope scope, string? status, string? severity,
        int? branchId, bool? suppressed, int? page, int? pageSize, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        var q = db.Alerts.AsNoTracking().Where(a => allowed == null || allowed.Contains(a.BranchId));
        q = status == "recent"
            ? q.Where(a => a.ClearedAt == null || a.RaisedAt > DateTimeOffset.UtcNow.AddHours(-24))
            : q.Where(a => a.ClearedAt == null);
        if (!string.IsNullOrEmpty(severity))
        {
            var sev = severity.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(a => sev.Contains(a.Severity));
        }
        if (branchId is { } b) q = q.Where(a => a.BranchId == b);
        if (suppressed != true) q = q.Where(a => !a.IsSuppressed);

        var size = Math.Clamp(pageSize ?? 100, 1, 500);
        var pg = Math.Max(page ?? 1, 1);
        var total = await q.CountAsync(ct);
        var items = await (from a in q
                           join br in db.Branches on a.BranchId equals br.BranchId
                           join d in db.Devices on a.DeviceId equals d.DeviceId into dj
                           from d in dj.DefaultIfEmpty()
                           join u in db.Users on a.AckByUserId equals u.UserId into uj
                           from u in uj.DefaultIfEmpty()
                           join ia in db.IncidentAlerts on a.AlertId equals ia.AlertId into iaj
                           from ia in iaj.DefaultIfEmpty()
                           join inc in db.Incidents on ia.IncidentId equals inc.IncidentId into ij
                           from inc in ij.DefaultIfEmpty()
                           orderby a.ClearedAt == null descending, a.RaisedAt descending
                           select new
                           {
                               a.AlertId, a.BranchId, BranchCode = br.BranchCode, BranchName = br.BranchName,
                               Device = d != null ? d.HostName : null, a.Severity, a.Title, a.LastValue,
                               a.RaisedAt, a.ClearedAt, a.IsSuppressed, a.SuppressReason,
                               AckBy = u != null ? u.DisplayName : null, a.AckAt, a.AckComment,
                               IncidentId = inc != null ? inc.IncidentId : (long?)null,
                               IncidentNo = inc != null ? inc.IncidentNo : null,
                           }).Skip((pg - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new { items, page = pg, pageSize = size, total });
    }

    private static async Task<IResult> AckAsync(long[] ids, string comment, HttpContext http, NmsDb db, BranchScope scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["comment"] = ["A comment is required to acknowledge"] });
        if (ids.Length == 0) return Results.NoContent();

        var allowed = await scope.AllowedAsync(http.User, ct);
        var alerts = await db.Alerts.Where(a => ids.Contains(a.AlertId) && a.AckAt == null).ToListAsync(ct);
        if (alerts.Any(a => allowed is not null && !allowed.Contains(a.BranchId))) return Results.Forbid();

        var now = DateTimeOffset.UtcNow;
        foreach (var a in alerts)
        {
            a.AckAt = now;
            a.AckByUserId = http.User.UserId();
            a.AckComment = comment.Trim();
            db.Record(http, "ALERT_ACK", "alert", a.AlertId, newValue: new { comment = a.AckComment });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { acknowledged = alerts.Count });
    }
}
