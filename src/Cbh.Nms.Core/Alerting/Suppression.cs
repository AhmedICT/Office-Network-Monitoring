using Cbh.Nms.Shared;

namespace Cbh.Nms.Core.Alerting;

public sealed class SuppressionContext
{
    public Dictionary<int, string> BranchStatus { get; init; } = [];
    public HashSet<int> BranchesInMaintenance { get; init; } = [];
    public HashSet<int> DevicesInMaintenance { get; init; } = [];
    /// <summary>Devices whose latest reachability sample is down.</summary>
    public HashSet<int> DevicesDown { get; init; } = [];
    /// <summary>device id → parent device ids.</summary>
    public Dictionary<int, List<int>> ParentsOf { get; init; } = [];
    /// <summary>Per branch, its top-level devices (router/firewall: no parent).</summary>
    public Dictionary<int, List<int>> RootDevicesOf { get; init; } = [];
}

public static class Suppression
{
    public const string Maintenance = "MAINTENANCE";
    public const string ParentDown = "PARENT_DOWN";
    public const string Flapping = "FLAPPING";

    /// <summary>FR-AL-03 / FR-AL-05: why an alert should be logged but not notified, or null.</summary>
    public static string? Decide(int branchId, string? entityType, int? deviceId, bool isBranchStatusAlert, SuppressionContext ctx)
    {
        if (ctx.BranchesInMaintenance.Contains(branchId)) return Maintenance;
        if (deviceId is { } d && ctx.DevicesInMaintenance.Contains(d)) return Maintenance;
        if (isBranchStatusAlert) return null;

        // The branch router is always the top parent (FR-BR-04): a whole-branch outage hides everything below it.
        if (ctx.BranchStatus.TryGetValue(branchId, out var status)
            && status is BranchStatus.OfflineIsp or BranchStatus.OfflinePower)
            return ParentDown;

        if (deviceId is { } dev && AnyAncestorDown(dev, ctx)) return ParentDown;

        // ISP / app checks depend on the branch's edge device(s).
        if (entityType is EntityTypes.IspLink or EntityTypes.App or EntityTypes.Branch
            && ctx.RootDevicesOf.TryGetValue(branchId, out var roots) && roots.Count > 0
            && roots.All(ctx.DevicesDown.Contains))
            return ParentDown;

        return null;
    }

    private static bool AnyAncestorDown(int deviceId, SuppressionContext ctx)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>(ctx.ParentsOf.GetValueOrDefault(deviceId) ?? []);
        while (stack.TryPop(out var p))
        {
            if (!seen.Add(p)) continue;
            if (ctx.DevicesDown.Contains(p)) return true;
            foreach (var gp in ctx.ParentsOf.GetValueOrDefault(p) ?? []) stack.Push(gp);
        }
        return false;
    }
}
