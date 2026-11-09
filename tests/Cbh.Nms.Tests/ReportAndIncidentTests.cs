using Cbh.Nms.Core;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Core.Incidents;
using Cbh.Nms.Core.Inventory;
using Cbh.Nms.Core.Notifications;
using Cbh.Nms.Core.Reports;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Tests;

public class AvailabilityCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(6));
    private static readonly DateTimeOffset End = Start.AddDays(30);   // 43,200 min

    [Fact]
    public void Always_online_is_100() =>
        Assert.Equal(100, AvailabilityCalculator.Compute(Start, End, [new(BranchStatus.Online, Start.AddDays(-5), null)], []).AvailabilityPct);

    [Fact]
    public void Offline_time_counts_degraded_and_unknown_do_not()
    {
        var history = new StatusInterval[]
        {
            new(BranchStatus.Online, Start, Start.AddDays(10)),
            new(BranchStatus.OfflineIsp, Start.AddDays(10), Start.AddDays(10).AddMinutes(216)),   // 0.5 % of 43,200 min
            new(BranchStatus.Degraded, Start.AddDays(10).AddMinutes(216), Start.AddDays(11)),
            new(BranchStatus.Unknown, Start.AddDays(11), Start.AddDays(12)),
            new(BranchStatus.Online, Start.AddDays(12), null),
        };
        var (pct, down) = AvailabilityCalculator.Compute(Start, End, history, []);
        Assert.Equal(TimeSpan.FromMinutes(216), down);
        Assert.Equal(99.5, pct);
    }

    [Fact]
    public void Maintenance_overlap_is_not_downtime()
    {
        var history = new StatusInterval[] { new(BranchStatus.OfflinePower, Start.AddDays(3), Start.AddDays(3).AddHours(2)) };
        var maint = new Window[] { new(Start.AddDays(3).AddHours(-1), Start.AddDays(3).AddHours(1)) };
        Assert.Equal(TimeSpan.FromHours(1), AvailabilityCalculator.Compute(Start, End, history, maint).Downtime);
    }

    [Fact]
    public void Outage_spanning_month_start_is_clipped()
    {
        var history = new StatusInterval[] { new(BranchStatus.OfflineIsp, Start.AddHours(-3), Start.AddHours(1)) };
        Assert.Equal(TimeSpan.FromHours(1), AvailabilityCalculator.Compute(Start, End, history, []).Downtime);
    }

    [Fact]
    public void Ongoing_outage_runs_to_period_end()
    {
        var history = new StatusInterval[] { new(BranchStatus.OfflinePower, End.AddHours(-2), null) };
        Assert.Equal(TimeSpan.FromHours(2), AvailabilityCalculator.Compute(Start, End, history, []).Downtime);
    }

    [Fact]
    public void Overlapping_maintenance_windows_are_merged() =>
        Assert.Single(AvailabilityCalculator.Merge([new(Start, Start.AddHours(2)), new(Start.AddHours(1), Start.AddHours(3))]));
}

public class IncidentTransitionTests
{
    [Theory]
    [InlineData("OPEN", "ACK")]
    [InlineData("OPEN", "IN_PROGRESS")]
    [InlineData("ACK", "RESOLVED")]
    [InlineData("RESOLVED", "CLOSED")]
    [InlineData("RESOLVED", "IN_PROGRESS")]   // reopen
    public void Allowed(string from, string to) => Assert.Null(IncidentTransitions.Check(from, to));

    [Theory]
    [InlineData("CLOSED", "OPEN")]
    [InlineData("IN_PROGRESS", "OPEN")]
    [InlineData("RESOLVED", "OPEN")]
    [InlineData("OPEN", "DONE")]
    public void Rejected(string from, string to) => Assert.NotNull(IncidentTransitions.Check(from, to));

    [Fact]
    public void Metrics_are_measured_from_detection()
    {
        var t0 = new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
        var i = new Incident { StartedAt = t0, DetectedAt = t0.AddMinutes(2), AckAt = t0.AddMinutes(11), ResolvedAt = t0.AddMinutes(49) };
        var m = IncidentMetrics.For(i);
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        Assert.Contains("\"mttdSeconds\":120", json);
        Assert.Contains("\"mttaSeconds\":540", json);
        Assert.Contains("\"mttrSeconds\":2820", json);
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("SAV01,Firewall,FORTINET,SAV01-FGT-01", 4)]
    [InlineData("\"a,b\",c", 2)]
    [InlineData("\"say \"\"hi\"\"\",x", 2)]
    public void Csv_split(string line, int fields) => Assert.Equal(fields, InventoryExtraEndpoints.SplitCsv(line).Length);

    [Fact]
    public void Csv_quotes_are_unescaped() => Assert.Equal("say \"hi\"", InventoryExtraEndpoints.SplitCsv("\"say \"\"hi\"\"\",x")[0]);

    [Theory]
    [InlineData(160, false, 1)]
    [InlineData(161, false, 2)]
    [InlineData(70, true, 1)]
    [InlineData(71, true, 2)]
    public void Sms_segments(int length, bool bangla, int expected) =>
        Assert.Equal(expected, HttpSmsGateway.Segments(new string(bangla ? 'ক' : 'a', length)));

    [Fact]
    public void Highest_role_from_ad_groups_wins()
    {
        var o = new AuthOptions
        {
            GroupRoleMap = new() { ["PDCL\\NMS-Engineers"] = Roles.BranchEngineer, ["NMS-Admins"] = Roles.SuperAdmin },
        };
        Assert.Equal(Roles.SuperAdmin, AuthEndpoints.MapRole(["PDCL\\nms-engineers", "PDCL\\NMS-Admins"], o));
        Assert.Equal(Roles.BranchEngineer, AuthEndpoints.MapRole(["PDCL\\NMS-Engineers"], o));
        Assert.Null(AuthEndpoints.MapRole(["PDCL\\Domain Users"], o));
    }

    [Fact]
    public void Break_glass_password_round_trip()
    {
        var o = new BreakGlassOptions { User = "nms-breakglass", PasswordHash = BreakGlassOptions.Hash("Correct-Horse-9", 1000) };
        Assert.True(o.Verify("NMS-BREAKGLASS", "Correct-Horse-9"));
        Assert.False(o.Verify("nms-breakglass", "wrong"));
        Assert.False(o.Verify("someone-else", "Correct-Horse-9"));
    }

    [Fact]
    public void Night_window_wraps_midnight_in_dhaka()
    {
        var planner = new NotificationPlanner(null!, Microsoft.Extensions.Options.Options.Create(new NotificationOptions()), TimeProvider.System);
        Assert.True(planner.IsNight(new DateTimeOffset(2026, 11, 9, 17, 30, 0, TimeSpan.Zero)));   // 23:30 Dhaka
        Assert.True(planner.IsNight(new DateTimeOffset(2026, 11, 9, 0, 30, 0, TimeSpan.Zero)));    // 06:30 Dhaka
        Assert.False(planner.IsNight(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero)));    // 07:00 Dhaka
    }
}
