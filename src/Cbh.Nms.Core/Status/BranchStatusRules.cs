using Cbh.Nms.Shared;

namespace Cbh.Nms.Core.Status;

/// <summary>Everything the §9.1 rules need about one branch, gathered by <see cref="StatusEngine"/>.</summary>
public sealed record BranchSignals
{
    public required DateTimeOffset Now { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    /// <summary>False when the branch has no MPLS router IP, so the Central Poller cannot check it.</summary>
    public bool PollerConfigured { get; init; }
    /// <summary>Start of the current run of failed Central Poller pings; null if the latest ping succeeded.</summary>
    public DateTimeOffset? PingFailingSince { get; init; }
    public bool? UpsOnBattery { get; init; }
    public double? UpsBatteryPct { get; init; }
    public int IspLinksDown { get; init; }
    public double? LossPct10m { get; init; }
    public double? LatencyMs10m { get; init; }
    public int AppChecksFailing { get; init; }
    public int CriticalDevicesDown { get; init; }
    public double LossThresholdPct { get; init; } = 5;
    public double LatencyThresholdMs { get; init; } = 300;
}

public sealed record BranchVerdict(string Status, string Reason);

public static class BranchStatusRules
{
    // NFR-01 wants an offline branch on the dashboard within 2 min. The poller pings every 30 s and
    // the agent heartbeats every 30 s, so 90 s = three missed in a row; plus a 15 s evaluation tick.
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(90);
    // FR-AV-05: agent silent for 3 min while the router still answers → monitoring lost, not offline.
    public static readonly TimeSpan MonitoringLostAfter = TimeSpan.FromMinutes(3);
    public const double LowBatteryPct = 10;

    /// <summary>SRS §9.1 — first matching rule wins; power is checked before ISP.</summary>
    public static BranchVerdict Evaluate(BranchSignals s)
    {
        var heartbeatAge = s.LastHeartbeat is { } hb ? s.Now - hb : TimeSpan.MaxValue;
        var pingDown = s.PingFailingSince is { } pf && s.Now - pf >= OfflineAfter;
        var silent = heartbeatAge >= OfflineAfter;

        if (s.PollerConfigured && pingDown && silent)
        {
            var powerWarning = s.UpsOnBattery == true || s.UpsBatteryPct < LowBatteryPct;
            return powerWarning
                ? new(BranchStatus.OfflinePower, "Router unreachable and agent silent; UPS was on battery / low")
                : new(BranchStatus.OfflineIsp, "Router unreachable over MPLS and agent silent");
        }

        if (heartbeatAge >= MonitoringLostAfter)
        {
            return s.PollerConfigured
                ? new(BranchStatus.Unknown, "Agent heartbeat missing; router still reachable")
                : new(BranchStatus.Unknown, "Agent heartbeat missing; no MPLS router IP to cross-check");
        }

        var reasons = new List<string>();
        if (s.IspLinksDown > 0) reasons.Add($"{s.IspLinksDown} ISP link(s) down");
        if (s.LossPct10m > s.LossThresholdPct) reasons.Add($"packet loss {s.LossPct10m:0.#}%");
        if (s.LatencyMs10m > s.LatencyThresholdMs) reasons.Add($"latency {s.LatencyMs10m:0} ms");
        if (s.AppChecksFailing > 0) reasons.Add($"{s.AppChecksFailing} HIS/app check(s) failing");
        if (s.CriticalDevicesDown > 0) reasons.Add($"{s.CriticalDevicesDown} critical device(s) down");

        return reasons.Count > 0
            ? new(BranchStatus.Degraded, string.Join("; ", reasons))
            : new(BranchStatus.Online, "All checks normal");
    }
}
