using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Incidents;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Admin;

public sealed record UserInput(string UserName, string DisplayName, string? Email, string? Mobile, string Role, bool IsActive,
    int[]? ScopeBranchIds, int[]? ScopeRegionIds);
public sealed record AlertRuleInput(string Name, string EntityType, string MetricKey, string Operator, double Threshold,
    int ForSeconds, string Severity, int? ScopeBranchId, bool IsEnabled);
public sealed record NotificationRuleInput(int? BranchId, string MinSeverity, string TimeWindow, int UserId, string Channels);
public sealed record EscalationInput(int AfterMinutes, string TargetRole, string Channels);
public sealed record TestMessage(string To);

/// <summary>SCR-09: users and scopes, rules, escalation, integrations, SMS spend, audit (FR-US-*, FR-AL-01/07).</summary>
public static class AdminEndpoints
{
    private static readonly string[] AllRoles =
        [Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager, Roles.NetAdmin, Roles.BranchEngineer, Roles.Management];
    private static readonly string[] Severities = [Severity.Critical, Severity.High, Severity.Warning, Severity.Info];

    public static void MapAdminApi(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(AuthPolicies.Admin);
        admin.MapGet("/users", ListUsers);
        admin.MapPost("/users", (UserInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveUser(null, i, h, db, ct));
        admin.MapPut("/users/{id:int}", (int id, UserInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveUser(id, i, h, db, ct));

        admin.MapGet("/alert-rules", (NmsDb db) => db.AlertRules.AsNoTracking().OrderBy(r => r.EntityType).ThenBy(r => r.Name).ToListAsync());
        admin.MapPost("/alert-rules", (AlertRuleInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveRule(null, i, h, db, ct));
        admin.MapPut("/alert-rules/{id:int}", (int id, AlertRuleInput i, HttpContext h, NmsDb db, CancellationToken ct) => SaveRule(id, i, h, db, ct));

        admin.MapGet("/notification-rules", (NmsDb db) => db.NotificationRules.AsNoTracking().ToListAsync());
        admin.MapPost("/notification-rules", SaveNotificationRule);
        admin.MapDelete("/notification-rules/{id:int}", DeleteNotificationRule);

        admin.MapGet("/escalation-policies", (NmsDb db) => db.EscalationPolicies.AsNoTracking().OrderBy(p => p.Severity).ThenBy(p => p.Level).ToListAsync());
        admin.MapPut("/escalation-policies/{id:int}", UpdateEscalation);

        admin.MapGet("/sms-usage", SmsUsage);
        admin.MapGet("/notifications", RecentNotifications);
        admin.MapGet("/integrations", IntegrationStatus);
        admin.MapPost("/integrations/test/email", TestEmail);
        admin.MapPost("/integrations/test/sms", TestSms);

        api.MapGet("/admin/audit", Audit).RequireAuthorization(AuthPolicies.Audit);
    }

    private static async Task<IResult> ListUsers(NmsDb db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        var scopes = await db.UserBranchScopes.AsNoTracking().ToListAsync(ct);
        return Results.Ok(users.Select(u => new
        {
            u.UserId, u.UserName, u.DisplayName, u.Email, u.Mobile, u.Role, u.IsActive, u.LastLoginAt,
            ScopeBranchIds = scopes.Where(s => s.UserId == u.UserId && s.BranchId != null).Select(s => s.BranchId),
            ScopeRegionIds = scopes.Where(s => s.UserId == u.UserId && s.RegionId != null).Select(s => s.RegionId),
        }));
    }

    private static async Task<IResult> SaveUser(int? id, UserInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.UserName)) errors["userName"] = ["Required (DOMAIN\\user)"];
        if (!AllRoles.Contains(i.Role)) errors["role"] = [$"One of {string.Join(", ", AllRoles)}"];
        if (await db.Users.AnyAsync(u => u.UserName == i.UserName.Trim() && u.UserId != id, ct)) errors["userName"] = ["Already exists"];
        if (id == h.User.UserId() && (i.Role != Roles.SuperAdmin || !i.IsActive)) errors["role"] = ["You cannot demote or deactivate yourself"];
        if (i.Role is Roles.ItManager or Roles.BranchEngineer && (i.ScopeBranchIds?.Length ?? 0) + (i.ScopeRegionIds?.Length ?? 0) == 0)
            errors["scope"] = ["IT Manager and Branch Engineer need at least one branch or region in scope (FR-US-02)"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var u = id is { } uid ? await db.Users.FirstOrDefaultAsync(x => x.UserId == uid, ct) : new AppUser();
        if (u is null) return Results.NotFound();
        var before = id is null ? null : new { u.UserName, u.Role, u.IsActive };
        u.UserName = i.UserName.Trim(); u.DisplayName = string.IsNullOrWhiteSpace(i.DisplayName) ? u.UserName : i.DisplayName.Trim();
        u.Email = i.Email; u.Mobile = i.Mobile; u.Role = i.Role; u.IsActive = i.IsActive;
        if (id is null) db.Users.Add(u);
        await db.SaveChangesAsync(ct);

        db.UserBranchScopes.RemoveRange(db.UserBranchScopes.Where(s => s.UserId == u.UserId));
        foreach (var b in i.ScopeBranchIds ?? []) db.UserBranchScopes.Add(new UserBranchScope { UserId = u.UserId, BranchId = b });
        foreach (var r in i.ScopeRegionIds ?? []) db.UserBranchScopes.Add(new UserBranchScope { UserId = u.UserId, RegionId = r });
        db.Record(h, id is null ? "USER_CREATE" : "USER_UPDATE", "user", u.UserId, before, i);
        await db.SaveChangesAsync(ct);
        return id is null ? Results.Created($"/api/v1/admin/users/{u.UserId}", new { u.UserId }) : Results.NoContent();
    }

    private static async Task<IResult> SaveRule(int? id, AlertRuleInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(i.Name)) errors["name"] = ["Required"];
        if (i.EntityType is not (EntityTypes.Branch or EntityTypes.Device or EntityTypes.IspLink or EntityTypes.App))
            errors["entityType"] = ["BRANCH, DEVICE, ISPLINK or APP"];
        if (string.IsNullOrWhiteSpace(i.MetricKey)) errors["metricKey"] = ["Required"];
        if (i.Operator is not (">" or ">=" or "<" or "<=" or "=")) errors["operator"] = ["One of > >= < <= ="];
        if (!Severities.Contains(i.Severity)) errors["severity"] = ["CRITICAL, HIGH, WARNING or INFO"];
        if (i.ForSeconds is < 0 or > 86400) errors["forSeconds"] = ["0 to 86400"];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var r = id is { } rid ? await db.AlertRules.FirstOrDefaultAsync(x => x.AlertRuleId == rid, ct) : new AlertRule();
        if (r is null) return Results.NotFound();
        var before = id is null ? null : new { r.Name, r.MetricKey, r.Operator, r.Threshold, r.ForSeconds, r.Severity, r.IsEnabled };
        r.Name = i.Name.Trim(); r.EntityType = i.EntityType; r.MetricKey = i.MetricKey.Trim(); r.Operator = i.Operator;
        r.Threshold = i.Threshold; r.ForSeconds = i.ForSeconds; r.Severity = i.Severity; r.ScopeBranchId = i.ScopeBranchId; r.IsEnabled = i.IsEnabled;
        if (id is null) db.AlertRules.Add(r);
        db.Record(h, id is null ? "RULE_CREATE" : "RULE_UPDATE", "alert_rule", id, before, i);
        await db.SaveChangesAsync(ct);   // FR-AL-01: takes effect on the next 15 s evaluation, no redeploy
        return id is null ? Results.Created($"/api/v1/admin/alert-rules/{r.AlertRuleId}", new { r.AlertRuleId }) : Results.NoContent();
    }

    private static async Task<IResult> SaveNotificationRule(NotificationRuleInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        if (!Severities.Contains(i.MinSeverity) || i.TimeWindow is not ("ANY" or "OFFICE" or "NIGHT")
            || i.Channels is not ("SMS" or "EMAIL" or "SMS+EMAIL") || !await db.Users.AnyAsync(u => u.UserId == i.UserId, ct))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = ["Check severity, time window, channels and user"] });
        var r = new NotificationRule { BranchId = i.BranchId, MinSeverity = i.MinSeverity, TimeWindow = i.TimeWindow, UserId = i.UserId, Channels = i.Channels };
        db.NotificationRules.Add(r);
        db.Record(h, "NOTIFRULE_CREATE", "notification_rule", null, newValue: i);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/admin/notification-rules/{r.NotificationRuleId}", new { r.NotificationRuleId });
    }

    private static async Task<IResult> DeleteNotificationRule(int id, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var r = await db.NotificationRules.FirstOrDefaultAsync(x => x.NotificationRuleId == id, ct);
        if (r is null) return Results.NotFound();
        db.NotificationRules.Remove(r);
        db.Record(h, "NOTIFRULE_DELETE", "notification_rule", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateEscalation(int id, EscalationInput i, HttpContext h, NmsDb db, CancellationToken ct)
    {
        var p = await db.EscalationPolicies.FirstOrDefaultAsync(x => x.EscalationPolicyId == id, ct);
        if (p is null) return Results.NotFound();
        if (i.AfterMinutes < 0 || !AllRoles.Contains(i.TargetRole) || i.Channels is not ("SMS" or "EMAIL" or "SMS+EMAIL"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Check minutes, role and channels"] });
        db.Record(h, "ESCALATION_UPDATE", "escalation_policy", id, new { p.AfterMinutes, p.TargetRole, p.Channels }, i);
        (p.AfterMinutes, p.TargetRole, p.Channels) = (i.AfterMinutes, i.TargetRole, i.Channels);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SmsUsage(NmsDb db, IOptions<NotificationOptions> opt, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(opt.Value.TimeZone);
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        var monthStart = new DateTimeOffset(new DateTime(local.Year, local.Month, 1), local.Offset).AddMonths(-5).ToUniversalTime();
        var rows = await db.NotificationLog.AsNoTracking().Where(n => n.Channel == "SMS" && n.At >= monthStart)
            .Select(n => new { n.At, n.Status, n.CostBdt }).ToListAsync(ct);
        var months = rows.GroupBy(r => TimeZoneInfo.ConvertTime(r.At, tz).ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                month = g.Key, sent = g.Count(r => r.Status == "SENT"), skipped = g.Count(r => r.Status == "SKIPPED"),
                failed = g.Count(r => r.Status == "FAILED"), costBdt = g.Where(r => r.Status == "SENT").Sum(r => r.CostBdt),
            }).ToList();
        var current = months.FirstOrDefault(m => m.month == local.ToString("yyyy-MM"));
        return Results.Ok(new
        {
            budgetBdt = opt.Value.SmsMonthlyBudgetBdt, dailyLimit = opt.Value.SmsDailyLimit,
            currentMonthCostBdt = current?.costBdt ?? 0, months,
        });
    }

    private static async Task<IResult> RecentNotifications(NmsDb db, string? channel, CancellationToken ct) =>
        Results.Ok(await db.NotificationLog.AsNoTracking().Where(n => channel == null || n.Channel == channel)
            .OrderByDescending(n => n.NotificationId).Take(200)
            .Select(n => new { n.NotificationId, n.At, n.Channel, n.Recipient, n.Subject, n.Status, n.Attempts, n.Error, n.CostBdt, n.SentAt })
            .ToListAsync(ct));

    private static IResult IntegrationStatus(IEmailSender email, ISmsGateway sms, TaskManagementClient tasks, IOptions<AuthOptions> auth) =>
        Results.Ok(new
        {
            email = email.Enabled, sms = sms.Enabled, taskManagement = tasks.Enabled,
            ldap = auth.Value.Ldap.Enabled, breakGlass = auth.Value.BreakGlass.Enabled,
        });

    private static async Task<IResult> TestEmail(TestMessage m, IEmailSender email, CancellationToken ct)
    {
        if (!email.Enabled) return Results.Problem("E-mail is not configured (Email:Host or Email:PickupDirectory)", statusCode: 400);
        try { await email.SendAsync(m.To, "CBH-NMS test e-mail", "If you can read this, SMTP is working.", null, ct); }
        catch (Exception ex) { return Results.Problem("Send failed: " + ex.Message, statusCode: 502); }
        return Results.Ok(new { sent = true });
    }

    private static async Task<IResult> TestSms(TestMessage m, ISmsGateway sms, CancellationToken ct)
    {
        if (!sms.Enabled) return Results.Problem("SMS gateway is not configured (Sms:Url)", statusCode: 400);
        try { await sms.SendAsync(m.To, "CBH-NMS test SMS / পরীক্ষামূলক বার্তা", ct); }
        catch (Exception ex) { return Results.Problem("Send failed: " + ex.Message, statusCode: 502); }
        return Results.Ok(new { sent = true });
    }

    private static async Task<IResult> Audit(NmsDb db, string? user, string? action, DateTimeOffset? from, DateTimeOffset? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        var q = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(user)) q = q.Where(a => a.UserName.Contains(user));
        if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action == action);
        if (from?.ToUniversalTime() is { } f) q = q.Where(a => a.At >= f);
        if (to?.ToUniversalTime() is { } t) q = q.Where(a => a.At < t);
        var size = Math.Clamp(pageSize ?? 100, 1, 500);
        var pg = Math.Max(page ?? 1, 1);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(a => a.AuditId).Skip((pg - 1) * size).Take(size)
            .Select(a => new { a.AuditId, a.At, a.UserName, ClientIp = a.ClientIp != null ? a.ClientIp.ToString() : null,
                               a.Action, a.EntityType, a.EntityId, a.OldValue, a.NewValue })
            .ToListAsync(ct);
        return Results.Ok(new { items, page = pg, pageSize = size, total });
    }
}
