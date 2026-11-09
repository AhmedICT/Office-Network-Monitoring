using System.Text.RegularExpressions;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Reports;

public static partial class ReportEndpoints
{
    public static void MapReportApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/reports").RequireAuthorization(AuthPolicies.Reports);
        g.MapGet("/availability", Availability);
        g.MapGet("/incidents", Incidents);
        g.MapGet("/isp", Isp);
        g.MapGet("/power", Power);
    }

    private static async Task<IResult> Availability(string? month, string? format, int? regionId, int? branchId,
        HttpContext http, ReportService svc, BranchScope scope, NmsDb db, CancellationToken ct)
    {
        if (ParseMonth(month) is not (int y, int m)) return BadMonth();
        var t = await svc.AvailabilityAsync(y, m, await scope.AllowedAsync(http.User, ct), regionId, branchId, ct);
        return Export(t, format, $"availability-{y}-{m:00}", http, db);
    }

    private static async Task<IResult> Incidents(DateTimeOffset? from, DateTimeOffset? to, string? month, string? format,
        int? regionId, int? branchId, HttpContext http, ReportService svc, BranchScope scope, NmsDb db, CancellationToken ct)
    {
        DateTimeOffset start, end;
        if (from is { } f && to is { } t2) (start, end) = (f, t2);
        else if (ParseMonth(month) is (int y, int m)) (start, end) = svc.MonthRange(y, m);
        else return BadMonth();
        var t = await svc.IncidentsAsync(start, end, await scope.AllowedAsync(http.User, ct), regionId, branchId, ct);
        return Export(t, format, $"incidents-{start:yyyyMMdd}", http, db);
    }

    private static async Task<IResult> Isp(string? month, string? format, HttpContext http, ReportService svc, BranchScope scope, NmsDb db, CancellationToken ct)
    {
        if (ParseMonth(month) is not (int y, int m)) return BadMonth();
        return Export(await svc.IspAsync(y, m, await scope.AllowedAsync(http.User, ct), ct), format, $"isp-{y}-{m:00}", http, db);
    }

    private static async Task<IResult> Power(string? month, string? format, int? regionId, HttpContext http, ReportService svc,
        BranchScope scope, NmsDb db, CancellationToken ct)
    {
        if (ParseMonth(month) is not (int y, int m)) return BadMonth();
        return Export(await svc.PowerAsync(y, m, await scope.AllowedAsync(http.User, ct), regionId, ct), format, $"power-{y}-{m:00}", http, db);
    }

    private static IResult Export(ReportTable t, string? format, string name, HttpContext http, NmsDb db)
    {
        switch (format?.ToLowerInvariant())
        {
            case "xlsx":
                Audit(http, db, name + ".xlsx");
                return Results.File(ReportExporter.ToXlsx(t), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"cbhnms-{name}.xlsx");
            case "pdf":
                Audit(http, db, name + ".pdf");
                return Results.File(ReportExporter.ToPdf(t), "application/pdf", $"cbhnms-{name}.pdf");
            default:
                return Results.Ok(t);
        }
    }

    // FR-US-03: exports are audited.
    private static void Audit(HttpContext http, NmsDb db, string file)
    {
        db.Record(http, "REPORT_EXPORT", "report", file);
        db.SaveChanges();
    }

    private static (int, int)? ParseMonth(string? month)
    {
        if (string.IsNullOrEmpty(month))
        {
            var now = DateTime.UtcNow.AddHours(6);
            return (now.Year, now.Month);
        }
        var match = MonthRegex().Match(month);
        if (!match.Success) return null;
        var (y, m) = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        return m is >= 1 and <= 12 ? (y, m) : null;
    }

    private static IResult BadMonth() => Results.Problem("month must be YYYY-MM", statusCode: 400);

    [GeneratedRegex(@"^(\d{4})-(\d{2})$")]
    private static partial Regex MonthRegex();
}

/// <summary>FR-RP-05: on the 1st of each month (Asia/Dhaka) e-mails last month's availability report.</summary>
public sealed class MonthlyReportJob(IServiceScopeFactory scopes, IEmailSender email, IOptions<NotificationOptions> opt,
    ILogger<MonthlyReportJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await RunIfDueAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Monthly report failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(opt.Value.TimeZone));
        if (local.Day != 1 || local.Hour < 8 || !email.Enabled) return;
        var prev = local.AddMonths(-1);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NmsDb>();
        var flag = $"monthly-report:{prev:yyyy-MM}";
        if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ops.system_flag (flag_key) VALUES ({flag}) ON CONFLICT DO NOTHING", ct) == 0)
            return;

        var svc = scope.ServiceProvider.GetRequiredService<ReportService>();
        var table = await svc.AvailabilityAsync(prev.Year, prev.Month, null, null, null, ct);
        var attachments = new List<(string, byte[], string)>
        {
            ($"cbhnms-availability-{prev:yyyy-MM}.pdf", ReportExporter.ToPdf(table), "application/pdf"),
            ($"cbhnms-availability-{prev:yyyy-MM}.xlsx", ReportExporter.ToXlsx(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        };
        var below = table.Flagged.Count;
        var recipients = await db.Users.Where(u => u.IsActive && u.Email != null
            && (u.Role == Roles.HeadOfIt || u.Role == Roles.Management || u.Role == Roles.ItManager)).ToListAsync(ct);
        foreach (var u in recipients)
            await email.SendAsync(u.Email!, $"CBH-NMS availability report — {prev:MMMM yyyy}",
                $"Attached: branch availability for {prev:MMMM yyyy}.\n{below} branch(es) below SLA target.", attachments, ct);
        log.LogInformation("Monthly report for {Month} sent to {Count} recipients", prev.ToString("yyyy-MM"), recipients.Count);
    }
}
