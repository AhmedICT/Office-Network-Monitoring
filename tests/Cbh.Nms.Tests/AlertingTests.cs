using Cbh.Nms.Core.Alerting;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Tests;

public class RuleEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
    private static Sample S(int secondsAgo, double v) => new(Now.AddSeconds(-secondsAgo), v);

    [Fact]
    public void Four_consecutive_failed_pings_over_three_minutes_fire_isp_down()
    {
        // 60 s polling: t-180, t-120, t-60, t-0 all down → duration 180 s ≥ for 180 s.
        var samples = new[] { S(240, 1), S(180, 0), S(120, 0), S(60, 0), S(0, 0) };
        Assert.Equal(RuleState.Firing, RuleEvaluator.Evaluate(samples, "=", 0, 180, Now).State);
    }

    [Fact]
    public void Three_failed_pings_are_still_pending() =>
        Assert.Equal(RuleState.Pending, RuleEvaluator.Evaluate([S(180, 1), S(120, 0), S(60, 0), S(0, 0)], "=", 0, 180, Now).State);

    [Fact]
    public void A_good_sample_in_between_resets_the_duration() =>
        Assert.Equal(RuleState.Pending, RuleEvaluator.Evaluate([S(240, 0), S(180, 0), S(120, 1), S(60, 0), S(0, 0)], "=", 0, 180, Now).State);

    [Fact]
    public void Latest_normal_sample_clears() =>
        Assert.Equal(RuleState.Normal, RuleEvaluator.Evaluate([S(120, 0), S(60, 0), S(0, 1)], "=", 0, 180, Now).State);

    [Fact]
    public void Zero_duration_rules_fire_on_the_first_sample()
    {
        var (state, value) = RuleEvaluator.Evaluate([S(10, 1)], "=", 1, 0, Now);
        Assert.Equal(RuleState.Firing, state);
        Assert.Equal(1, value);
    }

    [Fact]
    public void Old_data_is_stale_not_normal() =>
        Assert.Equal(RuleState.Stale, RuleEvaluator.Evaluate([S(600, 0)], "=", 0, 0, Now).State);

    [Fact]
    public void No_data_is_stale() => Assert.Equal(RuleState.Stale, RuleEvaluator.Evaluate([], ">", 1, 0, Now).State);

    [Theory]
    [InlineData(">", 91, 90, true)]
    [InlineData(">", 90, 90, false)]
    [InlineData(">=", 90, 90, true)]
    [InlineData("<", 29, 30, true)]
    [InlineData("<=", 30, 30, true)]
    [InlineData("=", 1, 1, true)]
    [InlineData("=", 0, 1, false)]
    public void Operators(string op, double value, double threshold, bool expected) =>
        Assert.Equal(expected, RuleEvaluator.Matches(op, value, threshold));

    [Fact]
    public void Unordered_samples_are_handled() =>
        Assert.Equal(RuleState.Firing, RuleEvaluator.Evaluate([S(0, 95), S(600, 95), S(300, 95)], ">", 90, 600, Now).State);
}

public class SuppressionTests
{
    // Branch 1: FGT(10) → SW(11) → SRV(12), SW(11) → AP(13)
    private static SuppressionContext Ctx(params int[] down) => new()
    {
        BranchStatus = new() { [1] = BranchStatus.Online },
        DevicesDown = [.. down],
        ParentsOf = new() { [11] = [10], [12] = [11], [13] = [11] },
        RootDevicesOf = new() { [1] = [10] },
    };

    [Fact]
    public void Child_of_a_down_parent_is_suppressed_FR_AL_03() =>
        Assert.Equal(Suppression.ParentDown, Suppression.Decide(1, EntityTypes.Device, 12, false, Ctx(10, 11, 12)));

    [Fact]
    public void Grandparent_down_suppresses_too() =>
        Assert.Equal(Suppression.ParentDown, Suppression.Decide(1, EntityTypes.Device, 12, false, Ctx(10, 12)));

    [Fact]
    public void Device_down_with_healthy_parents_notifies() =>
        Assert.Null(Suppression.Decide(1, EntityTypes.Device, 12, false, Ctx(12)));

    [Fact]
    public void Router_itself_is_never_suppressed_by_its_own_state() =>
        Assert.Null(Suppression.Decide(1, EntityTypes.Device, 10, false, Ctx(10)));

    [Fact]
    public void Isp_and_app_alerts_are_suppressed_when_the_edge_router_is_down() =>
        Assert.Equal(Suppression.ParentDown, Suppression.Decide(1, EntityTypes.App, null, false, Ctx(10)));

    [Fact]
    public void Whole_branch_offline_suppresses_everything_but_the_branch_alert_acceptance_10_2()
    {
        var ctx = Ctx();
        ctx.BranchStatus[1] = BranchStatus.OfflinePower;
        Assert.Equal(Suppression.ParentDown, Suppression.Decide(1, EntityTypes.Device, 11, false, ctx));
        Assert.Equal(Suppression.ParentDown, Suppression.Decide(1, EntityTypes.IspLink, null, false, ctx));
        Assert.Null(Suppression.Decide(1, EntityTypes.Branch, 1, true, ctx));
    }

    [Fact]
    public void Maintenance_suppresses_even_branch_status_alerts_FR_AL_05()
    {
        var ctx = Ctx();
        ctx.BranchesInMaintenance.Add(1);
        Assert.Equal(Suppression.Maintenance, Suppression.Decide(1, EntityTypes.Branch, 1, true, ctx));
    }

    [Fact]
    public void Device_maintenance_only_affects_that_device()
    {
        var ctx = Ctx();
        ctx.DevicesInMaintenance.Add(12);
        Assert.Equal(Suppression.Maintenance, Suppression.Decide(1, EntityTypes.Device, 12, false, ctx));
        Assert.Null(Suppression.Decide(1, EntityTypes.Device, 13, false, ctx));
    }
}
