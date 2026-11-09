using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Notifications;

/// <summary>
/// Decides who gets told what, on which channel (FR-AL-06/07, §9.2), and queues rows in
/// ops.notification_log. Delivery is done by <see cref="NotificationSender"/>.
/// Callers save the DbContext.
/// </summary>
public sealed class NotificationPlanner(NmsDb db, IOptions<NotificationOptions> options, TimeProvider clock)
{
    private readonly NotificationOptions _opt = options.Value;
    private List<AppUser>? _users;
    private List<(int UserId, int? RegionId, int? BranchId)>? _scopes;
    private Dictionary<int, int>? _branchRegion;

    public static bool SmsAllowed(string severity) => severity is Severity.Critical or Severity.High;

    public bool IsNight(DateTimeOffset utc)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_opt.TimeZone);
        var h = TimeZoneInfo.ConvertTime(utc, tz).Hour;
        return _opt.NightStartHour > _opt.NightEndHour
            ? h >= _opt.NightStartHour || h < _opt.NightEndHour
            : h >= _opt.NightStartHour && h < _opt.NightEndHour;
    }

    public void AlertRaised(Alert a)
    {
        if (a.Severity == Severity.Info) return;   // dashboard + log only
        var now = clock.GetUtcNow();
        var subject = $"[{a.Severity}] {a.Title}";
        var body = $"{a.Title}\nSeverity: {a.Severity}\nRaised: {Local(a.RaisedAt)}\n{_opt.PublicBaseUrl}alerts";
        var sms = $"CBH-NMS {a.Severity}: {a.Title} @{Local(a.RaisedAt):HH:mm}";
        var digest = a.Severity == Severity.Warning && IsNight(now);

        foreach (var (user, channels) in RecipientsFor(a.BranchId, a.Severity, now))
        {
            if (channels.Contains("EMAIL") && user.Email is { Length: > 0 })
                Queue("EMAIL", user.Email, user.UserId, subject, body, alertId: a.AlertId, digest: digest);
            if (channels.Contains("SMS") && SmsAllowed(a.Severity) && user.Mobile is { Length: > 0 })
                Queue("SMS", user.Mobile, user.UserId, null, sms, alertId: a.AlertId);
        }
        a.LastNotifiedAt = now;
    }

    public void AlertCleared(Alert a)
    {
        if (!SmsAllowed(a.Severity)) return;   // recovery mail only for alerts that paged people
        var subject = $"[RECOVERED] {a.Title}";
        var body = $"{a.Title}\nRecovered: {Local(a.ClearedAt ?? clock.GetUtcNow())}\nDuration: {Duration(a)}";
        foreach (var (user, channels) in RecipientsFor(a.BranchId, a.Severity, clock.GetUtcNow()))
            if (channels.Contains("EMAIL") && user.Email is { Length: > 0 })
                Queue("EMAIL", user.Email, user.UserId, subject, body, alertId: a.AlertId);
    }

    /// <summary>FR-AL-08 + §9.2: re-send SMS for unacknowledged Critical/High alerts every RepeatMinutes.</summary>
    public async Task RepeatUnackedAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_opt.RepeatMinutes <= 0) return;
        var cutoff = now.AddMinutes(-_opt.RepeatMinutes);
        var due = await db.Alerts.Where(a => a.ClearedAt == null && a.AckAt == null && !a.IsSuppressed
                && (a.Severity == Severity.Critical || a.Severity == Severity.High)
                && a.LastNotifiedAt != null && a.LastNotifiedAt <= cutoff)
            .ToListAsync(ct);
        foreach (var a in due)
        {
            var sms = $"CBH-NMS REMINDER {a.Severity}: {a.Title} (since {Local(a.RaisedAt):HH:mm}, not acknowledged)";
            foreach (var (user, channels) in RecipientsFor(a.BranchId, a.Severity, now))
                if (channels.Contains("SMS") && user.Mobile is { Length: > 0 })
                    Queue("SMS", user.Mobile, user.UserId, null, sms, alertId: a.AlertId);
            a.LastNotifiedAt = now;
        }
    }

    /// <summary>Escalation (FR-IN-05): notify everyone holding the target role who can see the branch.</summary>
    public void Escalate(Incident i, EscalationPolicy p)
    {
        var subject = $"[ESCALATION L{p.Level}] {i.IncidentNo} {i.Title}";
        var text = $"{i.IncidentNo} ({i.Severity}) not acknowledged for {p.AfterMinutes} min: {i.Title}";
        foreach (var u in Users().Where(u => u.Role == p.TargetRole && CanSee(u, i.BranchId)))
        {
            if (p.Channels.Contains("EMAIL") && u.Email is { Length: > 0 })
                Queue("EMAIL", u.Email, u.UserId, subject, $"{text}\n{_opt.PublicBaseUrl}incidents/{i.IncidentId}", incidentId: i.IncidentId);
            if (p.Channels.Contains("SMS") && u.Mobile is { Length: > 0 })
                Queue("SMS", u.Mobile, u.UserId, null, "CBH-NMS " + text, incidentId: i.IncidentId);
        }
    }

    public void QueueEmail(string to, int? userId, string subject, string body) => Queue("EMAIL", to, userId, subject, body);

    // Level-1 audience (§9.2): the branch's engineers + NOC (Net Admins), plus any notification_rule matches.
    private IEnumerable<(AppUser User, string Channels)> RecipientsFor(int branchId, string severity, DateTimeOffset now)
    {
        var defaultChannels = SmsAllowed(severity) ? "SMS+EMAIL" : "EMAIL";
        var result = new Dictionary<int, (AppUser, string)>();
        foreach (var u in Users().Where(u => u.Role is Roles.BranchEngineer or Roles.NetAdmin && CanSee(u, branchId)))
            result[u.UserId] = (u, defaultChannels);

        var window = IsNight(now) ? "NIGHT" : "OFFICE";
        foreach (var r in Rules().Where(r => (r.BranchId == null || r.BranchId == branchId)
                                             && Rank(severity) >= Rank(r.MinSeverity)
                                             && (r.TimeWindow == "ANY" || r.TimeWindow == window)))
            if (Users().FirstOrDefault(u => u.UserId == r.UserId) is { } u)
                result[u.UserId] = (u, r.Channels);
        return result.Values;
    }

    private static int Rank(string s) => s switch
    {
        Severity.Critical => 4, Severity.High => 3, Severity.Warning => 2, _ => 1,
    };

    private List<NotificationRule>? _rules;
    private List<NotificationRule> Rules() => _rules ??= db.NotificationRules.AsNoTracking().ToList();

    private List<AppUser> Users() => _users ??= db.Users.AsNoTracking().Where(u => u.IsActive).ToList();

    private bool CanSee(AppUser u, int branchId)
    {
        if (u.Role is not (Roles.ItManager or Roles.BranchEngineer)) return true;
        _scopes ??= db.UserBranchScopes.AsNoTracking().Select(s => new { s.UserId, s.RegionId, s.BranchId })
            .AsEnumerable().Select(s => (s.UserId, s.RegionId, s.BranchId)).ToList();
        _branchRegion ??= db.Branches.AsNoTracking().ToDictionary(b => b.BranchId, b => b.RegionId);
        var region = _branchRegion.GetValueOrDefault(branchId);
        return _scopes.Any(s => s.UserId == u.UserId && (s.BranchId == branchId || s.RegionId == region));
    }

    private void Queue(string channel, string to, int? userId, string? subject, string body,
        long? alertId = null, long? incidentId = null, bool digest = false)
    {
        db.NotificationLog.Add(new NotificationLog
        {
            Channel = channel, Recipient = to, UserId = userId, Subject = subject, Body = body,
            AlertId = alertId, IncidentId = incidentId, Status = "QUEUED", IsDigest = digest,
            NextAttemptAt = digest ? null : clock.GetUtcNow(),
        });
    }

    private DateTimeOffset Local(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(_opt.TimeZone));

    private static string Duration(Alert a)
    {
        var d = (a.ClearedAt ?? a.RaisedAt) - a.RaisedAt;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{(int)d.TotalMinutes} min";
    }
}
