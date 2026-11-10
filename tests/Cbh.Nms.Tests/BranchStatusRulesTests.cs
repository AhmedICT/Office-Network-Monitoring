using Cbh.Nms.Core.Status;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Tests;

public class BranchStatusRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    private static BranchSignals Healthy() => new()
    {
        Now = Now,
        LastHeartbeat = Now.AddSeconds(-20),
        HoPaths = [new(HoPathNames.Mpls)],
        UpsOnBattery = false,
        UpsBatteryPct = 100,
        LossPct10m = 0.2,
        LatencyMs10m = 18,
    };

    private static string Status(BranchSignals s) => BranchStatusRules.Evaluate(s).Status;

    [Fact]
    public void All_normal_is_online() => Assert.Equal(BranchStatus.Online, Status(Healthy()));

    [Fact]
    public void Router_down_and_agent_silent_with_ups_on_battery_is_offline_power() =>
        Assert.Equal(BranchStatus.OfflinePower, Status(Healthy() with
        {
            LastHeartbeat = Now.AddMinutes(-2), HoPaths = [new(HoPathNames.Mpls, Now.AddMinutes(-2))], UpsOnBattery = true,
        }));

    [Fact]
    public void Low_battery_alone_counts_as_power_warning() =>
        Assert.Equal(BranchStatus.OfflinePower, Status(Healthy() with
        {
            LastHeartbeat = Now.AddMinutes(-2), HoPaths = [new(HoPathNames.Mpls, Now.AddMinutes(-2))], UpsBatteryPct = 4,
        }));

    [Fact]
    public void Router_down_and_agent_silent_without_ups_warning_is_offline_isp() =>
        Assert.Equal(BranchStatus.OfflineIsp, Status(Healthy() with
        {
            LastHeartbeat = Now.AddMinutes(-2), HoPaths = [new(HoPathNames.Mpls, Now.AddMinutes(-2))],
        }));

    [Fact]
    public void Unknown_ups_state_is_classified_as_isp() =>
        Assert.Equal(BranchStatus.OfflineIsp, Status(Healthy() with
        {
            LastHeartbeat = Now.AddMinutes(-2), HoPaths = [new(HoPathNames.Mpls, Now.AddMinutes(-2))], UpsOnBattery = null, UpsBatteryPct = null,
        }));

    [Fact]
    public void Offline_is_detected_within_two_minutes_NFR01()
    {
        // Outage begins at T0: last heartbeat arrived just before, first failed poller ping just after.
        var t0 = Now;
        var s = Healthy() with { LastHeartbeat = t0, HoPaths = [new(HoPathNames.Mpls, t0)] };
        Assert.NotEqual(BranchStatus.OfflineIsp, Status(s with { Now = t0.AddSeconds(60) }));
        // Worst case adds one 15 s evaluation tick on top of the threshold.
        var detectedBy = t0 + BranchStatusRules.OfflineAfter + StatusEngine.Interval;
        Assert.True(detectedBy - t0 <= TimeSpan.FromMinutes(2));
        Assert.Equal(BranchStatus.OfflineIsp, Status(s with { Now = t0 + BranchStatusRules.OfflineAfter }));
    }

    [Fact]
    public void Ping_failing_briefly_is_not_offline() =>
        Assert.Equal(BranchStatus.Online, Status(Healthy() with { HoPaths = [new(HoPathNames.Mpls, Now.AddSeconds(-30))] }));

    [Fact]
    public void Agent_silent_but_router_reachable_is_unknown_not_offline_FR_AV_05() =>
        Assert.Equal(BranchStatus.Unknown, Status(Healthy() with { LastHeartbeat = Now.AddMinutes(-4) }));

    [Fact]
    public void Agent_silent_less_than_three_minutes_with_router_up_keeps_last_assessment() =>
        Assert.Equal(BranchStatus.Online, Status(Healthy() with { LastHeartbeat = Now.AddMinutes(-2) }));

    [Fact]
    public void Without_poller_a_silent_agent_is_unknown_never_offline()
    {
        var s = Healthy() with { HoPaths = [], LastHeartbeat = Now.AddMinutes(-10) };
        Assert.Equal(BranchStatus.Unknown, Status(s));
    }

    [Fact]
    public void Never_seen_agent_is_unknown() =>
        Assert.Equal(BranchStatus.Unknown, Status(Healthy() with { LastHeartbeat = null }));

    [Theory]
    [InlineData(1, 0, 18, 0, 0)]      // one ISP down
    [InlineData(0, 6, 18, 0, 0)]      // loss > 5 %
    [InlineData(0, 0, 320, 0, 0)]     // latency > 300 ms
    [InlineData(0, 0, 18, 1, 0)]      // HIS check failing
    [InlineData(0, 0, 18, 0, 1)]      // critical device down
    public void Partial_problems_are_degraded(int ispDown, double loss, double latency, int appsFailing, int critDown) =>
        Assert.Equal(BranchStatus.Degraded, Status(Healthy() with
        {
            IspLinksDown = ispDown, LossPct10m = loss, LatencyMs10m = latency,
            AppChecksFailing = appsFailing, CriticalDevicesDown = critDown,
        }));

    [Fact]
    public void Thresholds_are_per_branch() =>
        Assert.Equal(BranchStatus.Online, Status(Healthy() with { LatencyMs10m = 320, LatencyThresholdMs = 400 }));

    [Fact]
    public void Exactly_at_threshold_is_not_degraded() =>
        Assert.Equal(BranchStatus.Online, Status(Healthy() with { LossPct10m = 5, LatencyMs10m = 300 }));

    [Fact]
    public void Degraded_reason_lists_every_problem()
    {
        var v = BranchStatusRules.Evaluate(Healthy() with { IspLinksDown = 1, LatencyMs10m = 342 });
        Assert.Contains("ISP", v.Reason);
        Assert.Contains("latency 342 ms", v.Reason);
    }

    // ── D-13: MPLS + VPN paths ──

    private static BranchSignals DualPath(DateTimeOffset? mplsFailing = null, DateTimeOffset? vpnFailing = null) =>
        Healthy() with { HoPaths = [new(HoPathNames.Mpls, mplsFailing), new(HoPathNames.Vpn, vpnFailing)] };

    [Fact]
    public void Mpls_down_with_vpn_up_is_degraded_not_offline()
    {
        var v = BranchStatusRules.Evaluate(DualPath(mplsFailing: Now.AddMinutes(-5)) with { LastHeartbeat = Now.AddMinutes(-2) });
        Assert.Equal(BranchStatus.Degraded, v.Status);
        Assert.Contains("MPLS path to HO down — running on VPN", v.Reason);
        Assert.Equal(["MPLS"], v.HoPathsDown);
    }

    [Fact]
    public void Vpn_down_with_mpls_up_is_degraded() =>
        Assert.Equal(BranchStatus.Degraded, Status(DualPath(vpnFailing: Now.AddMinutes(-3))));

    [Fact]
    public void Both_paths_down_and_agent_silent_is_offline_isp()
    {
        var v = BranchStatusRules.Evaluate(DualPath(Now.AddMinutes(-2), Now.AddMinutes(-2)) with { LastHeartbeat = Now.AddMinutes(-2) });
        Assert.Equal(BranchStatus.OfflineIsp, v.Status);
        Assert.Contains("MPLS and VPN", v.Reason);
        Assert.Equal(2, v.HoPathsDown.Count);
    }

    [Fact]
    public void Both_paths_down_with_ups_on_battery_is_offline_power() =>
        Assert.Equal(BranchStatus.OfflinePower, Status(DualPath(Now.AddMinutes(-2), Now.AddMinutes(-2))
            with { LastHeartbeat = Now.AddMinutes(-2), UpsOnBattery = true }));

    [Fact]
    public void One_path_down_and_agent_silent_is_unknown_because_branch_still_answers() =>
        Assert.Equal(BranchStatus.Unknown, Status(DualPath(mplsFailing: Now.AddMinutes(-5)) with { LastHeartbeat = Now.AddMinutes(-5) }));

    [Fact]
    public void Brief_path_failure_is_ignored()
    {
        var v = BranchStatusRules.Evaluate(DualPath(mplsFailing: Now.AddSeconds(-45)));
        Assert.Equal(BranchStatus.Online, v.Status);
        Assert.Empty(v.HoPathsDown);
    }

    [Fact]
    public void Vpn_only_branch_goes_offline_when_vpn_down() =>
        Assert.Equal(BranchStatus.OfflineIsp, Status(Healthy() with
        {
            HoPaths = [new(HoPathNames.Vpn, Now.AddMinutes(-2))], LastHeartbeat = Now.AddMinutes(-2),
        }));
}
