using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Notifications;

/// <summary>
/// Delivers queued ops.notification_log rows. Retries at 1, 5, 15 minutes (§9.3), enforces the SMS
/// daily cap and monthly budget (§2.4, INT-03), sends the 07:00 Warning digest, and warns the
/// Head of IT once a month when SMS spend reaches 80 %.
/// </summary>
public sealed class NotificationSender(IServiceScopeFactory scopes, IEmailSender email, ISmsGateway sms,
    IOptions<NotificationOptions> notifOpt, IOptions<SmsOptions> smsOpt, TimeProvider clock,
    ILogger<NotificationSender> log) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Notification sender failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDb>();
        var planner = scope.ServiceProvider.GetRequiredService<NotificationPlanner>();
        var now = clock.GetUtcNow();
        var tz = TimeZoneInfo.FindSystemTimeZoneById(notifOpt.Value.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var dayStartUtc = new DateTimeOffset(localNow.Date, localNow.Offset).ToUniversalTime();
        var monthStartUtc = new DateTimeOffset(new DateTime(localNow.Year, localNow.Month, 1), localNow.Offset).ToUniversalTime();

        if (!planner.IsNight(now)) await SendDigestAsync(db, now, ct);

        var due = await db.NotificationLog
            .Where(n => n.Status == "QUEUED" && !n.IsDigest && n.NextAttemptAt != null && n.NextAttemptAt <= now)
            .OrderBy(n => n.NotificationId).Take(50).ToListAsync(ct);
        if (due.Count == 0) return;

        var smsToday = await db.NotificationLog.CountAsync(n => n.Channel == "SMS" && n.Status == "SENT" && n.At >= dayStartUtc, ct);
        var smsMonthCost = await db.NotificationLog.Where(n => n.Channel == "SMS" && n.Status == "SENT" && n.At >= monthStartUtc)
            .SumAsync(n => (decimal?)n.CostBdt, ct) ?? 0;

        foreach (var n in due)
        {
            try
            {
                if (n.Channel == "SMS")
                {
                    if (!sms.Enabled) { n.Status = "SKIPPED"; n.Error = "SMS gateway not configured"; continue; }
                    if (smsToday >= notifOpt.Value.SmsDailyLimit) { n.Status = "SKIPPED"; n.Error = "Daily SMS limit reached"; continue; }
                    if (smsMonthCost >= notifOpt.Value.SmsMonthlyBudgetBdt) { n.Status = "SKIPPED"; n.Error = "Monthly SMS budget exhausted"; continue; }
                    await sms.SendAsync(n.Recipient, n.Body ?? "", ct);
                    n.CostBdt = HttpSmsGateway.Segments(n.Body ?? "") * smsOpt.Value.CostPerSmsBdt;
                    smsToday++;
                    smsMonthCost += n.CostBdt;
                }
                else
                {
                    if (!email.Enabled) { n.Status = "SKIPPED"; n.Error = "E-mail not configured"; continue; }
                    await email.SendAsync(n.Recipient, n.Subject ?? "CBH-NMS", n.Body ?? "", null, ct);
                }
                n.Status = "SENT";
                n.SentAt = now;
                n.Error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                n.Attempts++;
                n.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (n.Attempts > RetryDelays.Length) n.Status = "FAILED";
                else n.NextAttemptAt = now + RetryDelays[n.Attempts - 1];
                log.LogWarning("{Channel} to {To} failed (attempt {Attempt}): {Error}", n.Channel, n.Recipient, n.Attempts, n.Error);
            }
        }

        await WarnBudgetAsync(db, planner, smsMonthCost, localNow, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Night-time Warning e-mails are held and sent as one digest per recipient (§9.2).</summary>
    private async Task SendDigestAsync(NmsDb db, DateTimeOffset now, CancellationToken ct)
    {
        var held = await db.NotificationLog.Where(n => n.IsDigest && n.Status == "QUEUED").ToListAsync(ct);
        if (held.Count == 0) return;
        foreach (var group in held.GroupBy(n => n.Recipient))
        {
            var body = "Warnings raised overnight:\n\n" + string.Join("\n", group.Select(n => "• " + n.Subject));
            if (email.Enabled)
            {
                try { await email.SendAsync(group.Key, $"CBH-NMS overnight digest ({group.Count()} warnings)", body, null, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Digest to {To} failed: {Error}", group.Key, ex.Message);
                    continue;
                }
            }
            foreach (var n in group)
            {
                n.Status = email.Enabled ? "SENT" : "SKIPPED";
                n.SentAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task WarnBudgetAsync(NmsDb db, NotificationPlanner planner, decimal monthCost, DateTimeOffset localNow, CancellationToken ct)
    {
        var budget = notifOpt.Value.SmsMonthlyBudgetBdt;
        if (budget <= 0 || monthCost < budget * 0.8m) return;
        var flag = $"sms-budget-80:{localNow:yyyy-MM}";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ops.system_flag (flag_key) VALUES ({flag}) ON CONFLICT DO NOTHING", ct);
        if (inserted == 0) return;
        foreach (var u in await db.Users.Where(u => u.IsActive && u.Role == Roles.HeadOfIt && u.Email != null).ToListAsync(ct))
            planner.QueueEmail(u.Email!, u.UserId, "CBH-NMS: SMS budget 80% used",
                $"SMS spend this month is ৳{monthCost:0.00} of the ৳{budget:0} budget.");
    }
}
