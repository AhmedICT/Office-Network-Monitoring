using System.Security.Claims;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core;

/// <summary>Role policies from SRS §9.4.</summary>
public static class AuthPolicies
{
    public const string Agent = "agent";
    public const string Viewer = "viewer";
    public const string EditInventory = "edit-inventory";   // branch/device edit, agents
    public const string Ack = "ack";                         // alert ack, incident update
    public const string Admin = "admin";
    public const string Maintenance = "maintenance";           // also: close incident
    public const string ApproveMaintenance = "approve-maintenance";
    public const string Reports = "reports";
    public const string Audit = "audit";

    public static void Add(AuthorizationOptions o)
    {
        o.AddPolicy(Agent, p => p.AddAuthenticationSchemes(AgentAuthHandler.SchemeName).RequireClaim(AgentAuthHandler.AgentClaim));
        o.AddPolicy(Viewer, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager, Roles.NetAdmin, Roles.BranchEngineer, Roles.Management));
        o.AddPolicy(EditInventory, p => p.RequireRole(Roles.SuperAdmin, Roles.NetAdmin));
        o.AddPolicy(Ack, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager, Roles.NetAdmin, Roles.BranchEngineer));
        o.AddPolicy(Admin, p => p.RequireRole(Roles.SuperAdmin));
        o.AddPolicy(Maintenance, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager));
        o.AddPolicy(ApproveMaintenance, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt));
        o.AddPolicy(Reports, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager, Roles.NetAdmin, Roles.Management));
        o.AddPolicy(Audit, p => p.RequireRole(Roles.SuperAdmin, Roles.HeadOfIt));
    }
}

/// <summary>
/// Maps the Windows/AD identity to its sec.app_user row and adds the role + user-id claims.
/// AD group → role sync (FR-US-01) will populate sec.app_user; until then users are created by an admin.
/// </summary>
public sealed class AppUserClaimsTransformation(NmsDb db) : IClaimsTransformation
{
    public const string UserIdClaim = "nms:user_id";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true, Name: { } name } identity
            || identity.AuthenticationType == AgentAuthHandler.SchemeName
            || principal.HasClaim(c => c.Type == UserIdClaim))
            return principal;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == name && u.IsActive);
        if (user is null) return principal;   // authenticated but not provisioned → no role → 403

        var extra = new ClaimsIdentity(
            [new Claim(UserIdClaim, user.UserId.ToString()), new Claim(ClaimTypes.Role, user.Role)]);
        principal.AddIdentity(extra);
        return principal;
    }
}

public static class UserPrincipal
{
    public static int? UserId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(AppUserClaimsTransformation.UserIdClaim), out var id) ? id : null;

    /// <summary>Roles marked "S" in §9.4 see only their scoped branches.</summary>
    public static bool IsScoped(this ClaimsPrincipal p) =>
        p.IsInRole(Roles.ItManager) || p.IsInRole(Roles.BranchEngineer);
}
