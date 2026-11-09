using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Alerting;

/// <summary>Human-readable names for alert titles ("SAV01 · SAV01-FGT-01").</summary>
public sealed class EntityLabels
{
    private Dictionary<int, (string Code, string Name, short Tier)> _branches = [];
    private Dictionary<int, string> _devices = [];
    private Dictionary<int, string> _links = [];
    private Dictionary<int, string> _apps = [];

    public static async Task<EntityLabels> LoadAsync(NmsDb db, CancellationToken ct) => new()
    {
        _branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, b => (b.BranchCode, b.BranchName, b.Tier), ct),
        _devices = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.DeviceId, d => d.HostName, ct),
        _links = await db.IspLinks.AsNoTracking().Include(l => l.Isp)
            .ToDictionaryAsync(l => l.IspLinkId, l => $"{l.Isp!.Name} ({l.LinkRole.ToLowerInvariant()})", ct),
        _apps = await db.AppChecks.AsNoTracking().ToDictionaryAsync(a => a.AppCheckId, a => a.Name, ct),
    };

    public string Branch(int id) => _branches.TryGetValue(id, out var b) ? b.Code : $"branch {id}";
    public short Tier(int id) => _branches.TryGetValue(id, out var b) ? b.Tier : (short)2;

    public string Describe(string entityType, int id, int branchId) => entityType switch
    {
        EntityTypes.Device => $"{Branch(branchId)} · {_devices.GetValueOrDefault(id, $"device {id}")}",
        EntityTypes.IspLink => $"{Branch(branchId)} · {_links.GetValueOrDefault(id, $"link {id}")}",
        EntityTypes.App => $"{Branch(branchId)} · {_apps.GetValueOrDefault(id, $"check {id}")}",
        _ => Branch(branchId),
    };
}
