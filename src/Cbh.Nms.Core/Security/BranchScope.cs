using System.Security.Claims;
using Cbh.Nms.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core;

/// <summary>Server-side branch scoping (FR-US-02). Every user-facing query filters through this.</summary>
public sealed class BranchScope(NmsDb db)
{
    /// <summary>Branch ids the user may see, or null when the user sees all branches.</summary>
    public async Task<HashSet<int>?> AllowedAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!user.IsScoped()) return null;
        var userId = user.UserId();
        if (userId is null) return [];

        var scopes = db.UserBranchScopes.Where(s => s.UserId == userId);
        var ids = await db.Branches
            .Where(b => scopes.Any(s => s.BranchId == b.BranchId || s.RegionId == b.RegionId))
            .Select(b => b.BranchId).ToListAsync(ct);
        return [.. ids];
    }

    public async Task<bool> CanSeeAsync(ClaimsPrincipal user, int branchId, CancellationToken ct = default) =>
        await AllowedAsync(user, ct) is not { } allowed || allowed.Contains(branchId);
}
