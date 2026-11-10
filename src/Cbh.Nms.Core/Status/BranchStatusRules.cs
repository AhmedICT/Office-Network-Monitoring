using Cbh.Nms.Shared;

namespace Cbh.Nms.Core.Status;

/// <summary>The HO↔branch paths the Central Poller checks (D-13).</summary>
public static class HoPathNames
{
    public const string Mpls = "MPLS";
    public const string Vpn = "VPN";
}

/// <summary>One configured HO↔branch path and the start of its current run of failed pings (null = latest ping OK).</summary>
public sealed record HoPathSignal(string Path, DateTimeOffset? FailingSince = null);

/// <summary>Everything the §9.1 rules need about one branch, gathered by <see cref="StatusEngine"/>.</summary>
public sealed record BranchSignals
{
    public required DateTimeOffset Now { get; init; }
    public DateTimeOffset? LastHeartbeat { get; init; }
    /// <summary>The configured paths (MPLS and/or VPN). Empty when the branch has neither IP, so the
    /// Central Poller cannot check it.</summary>
    public IReadOnlyList<HoPathSignal> HoPaths { get; init; } = [];
    public bool? UpsOnBattery { get; init; }
    public double? UpsBatteryPct { get; init; }
    public int IspLinksDown { get; init; }
    public double? LossPct10m { get; init; }
    public double? LatencyMs10m { get; init; }
    public int AppChecksFailing { get; init; }
    public int CriticalDevicesDown { get; init; }
    public double LossThresholdPct { get; init; } = 5;
    public double LatencyThresholdMs { get; init; } = 300;

    public bool PollerConfigured => HoPaths.Count > 0;
}

/// <param name="HoPathsDown">Paths that have failed for at least <see cref="BranchStatusRules.OfflineAfter"/>.</param>
public sealed record BranchVerdict(string Status, string Reason, IReadOnlyList<string> HoPathsDown)
{
    public BranchVerdict(string status, string reason) : this(status, reason, []) { }
}

public static class BranchStatusRules
{
    // NFR-01 wants an offline branch on the dashboard within 2 min. The poller pings every 30 s and
    // the agent heartbeats every 30 s, so 90 s = three missed in a row; plus a 15 s evaluation tick.
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(90);
    // FR-AV-05: agent silent for 3 min while the router still answers → monitoring lost, not offline.
    public static readonly TimeSpan MonitoringLostAfter = TimeSpan.FromMinutes(3);
    public const double LowBatteryPct = 10;

    /// <summary>SRS §9.1 — first matching rule wins; power is checked before ISP. A branch is cut off from
    /// HO only when every configured path (MPLS, VPN) is down; one path down is Degraded.</summary>
    public static BranchVerdict Evaluate(BranchSignals s)
    {
        var heartbeatAge = s.LastHeartbeat is { } hb ? s.Now - hb : TimeSpan.MaxValue;
        var silent = heartbeatAge >= OfflineAfter;
        var down = s.HoPaths.Where(p => p.FailingSince is { } f && s.Now - f >= OfflineAfter).Select(p => p.Path).ToList();
        var up = s.HoPaths.Select(p => p.Path).Except(down).ToList();
        var allDown = s.PollerConfigured && up.Count == 0;

        if (allDown && silent)
        {
            var via = string.Join(" and ", down);
            var powerWarning = s.UpsOnBattery == true || s.UpsBatteryPct < LowBatteryPct;
            return powerWarning
                ? new(BranchStatus.OfflinePower, $"Router unreachable over {via} and agent silent; UPS was on battery / low", down)
                : new(BranchStatus.OfflineIsp, $"Router unreachable over {via} and agent silent", down);
        }

        if (heartbeatAge >= MonitoringLostAfter)
        {
            return s.PollerConfigured
                ? new(BranchStatus.Unknown, $"Agent heartbeat missing; router still reachable over {string.Join("/", up)}", down)
                : new(BranchStatus.Unknown, "Agent heartbeat missing; no MPLS or VPN router IP to cross-check", down);
        }

        var reasons = new List<string>();
        if (down.Count > 0)
            reasons.Add(up.Count > 0
                ? $"{string.Join("/", down)} path to HO down — running on {string.Join("/", up)}"
                : $"{string.Join("/", down)} path to HO down (Central Poller)");
        if (s.IspLinksDown > 0) reasons.Add($"{s.IspLinksDown} ISP link(s) down");
        if (s.LossPct10m > s.LossThresholdPct) reasons.Add($"packet loss {s.LossPct10m:0.#}%");
        if (s.LatencyMs10m > s.LatencyThresholdMs) reasons.Add($"latency {s.LatencyMs10m:0} ms");
        if (s.AppChecksFailing > 0) reasons.Add($"{s.AppChecksFailing} HIS/app check(s) failing");
        if (s.CriticalDevicesDown > 0) reasons.Add($"{s.CriticalDevicesDown} critical device(s) down");

        return reasons.Count > 0
            ? new(BranchStatus.Degraded, string.Join("; ", reasons), down)
            : new(BranchStatus.Online, "All checks normal", down);
    }
}
