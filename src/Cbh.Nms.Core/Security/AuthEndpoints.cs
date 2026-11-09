using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core;

public sealed class AuthOptions
{
    /// <summary>AD group (DOMAIN\Group or group CN) → role, highest first wins (FR-US-01).</summary>
    public Dictionary<string, string> GroupRoleMap { get; set; } = [];
    public LdapOptions Ldap { get; set; } = new();
    public BreakGlassOptions BreakGlass { get; set; } = new();
}

public sealed class LdapOptions
{
    public string? Server { get; set; }           // dc01.pdcl.local
    public int Port { get; set; } = 636;          // LDAPS only
    public string? Domain { get; set; }           // PDCL
    public string? BaseDn { get; set; }           // DC=pdcl,DC=local
    public bool Enabled => !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(BaseDn);
}

/// <summary>The single non-AD account (§2.4) for when AD is down. Hash = PBKDF2-SHA256 "iterations.salt.hash" (base64).</summary>
public sealed class BreakGlassOptions
{
    public string? User { get; set; }
    public string? PasswordHash { get; set; }
    public bool Enabled => !string.IsNullOrEmpty(User) && !string.IsNullOrEmpty(PasswordHash);

    public static string Hash(string password, int iterations = 210_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string user, string password)
    {
        if (!Enabled || !string.Equals(user, User, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = PasswordHash!.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iter)) return false;
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed record LoginRequest(string UserName, string Password);

/// <summary>
/// SCR-01. Browsers on domain PCs call /auth/windows (Negotiate) and never see a form; others post
/// to /auth/login (LDAPS bind, or the break-glass account). Both end in the same cookie session.
/// </summary>
public static class AuthEndpoints
{
    private static readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> Lockouts = new();
    private const int MaxFails = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    public static void MapAuthApi(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/auth");
        g.MapGet("/windows", WindowsLogin).RequireAuthorization(p =>
            p.AddAuthenticationSchemes(NegotiateDefaults.AuthenticationScheme).RequireAuthenticatedUser());
        g.MapPost("/login", FormLogin).AllowAnonymous();
        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
        g.MapGet("/options", (IOptions<AuthOptions> o) =>
            Results.Ok(new { formLogin = o.Value.Ldap.Enabled || o.Value.BreakGlass.Enabled })).AllowAnonymous();

        api.MapGet("/me", Me).RequireAuthorization(AuthPolicies.Viewer);
    }

    private static async Task<IResult> WindowsLogin(HttpContext http, NmsDb db, IOptions<AuthOptions> opt, ILoggerFactory lf)
    {
        var name = http.User.Identity!.Name!;
        string? mappedRole = null;
        if (OperatingSystem.IsWindows() && http.User.Identity is WindowsIdentity wi)
            mappedRole = MapRole(GroupNames(wi), opt.Value);
        return await SignInAsync(http, db, name, null, null, null, mappedRole, "WINDOWS", lf);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> GroupNames(WindowsIdentity wi)
    {
        var names = new List<string>();
        foreach (var g in wi.Groups ?? [])
        {
            try { names.Add(g.Translate(typeof(NTAccount)).Value); }
            catch (IdentityNotMappedException) { }
        }
        return names;
    }

    private static async Task<IResult> FormLogin(LoginRequest req, HttpContext http, NmsDb db, IOptions<AuthOptions> opt, ILoggerFactory lf)
    {
        var o = opt.Value;
        var key = req.UserName.Trim().ToLowerInvariant();
        if (Lockouts.TryGetValue(key, out var l) && l.Until > DateTimeOffset.UtcNow)
            return Results.Problem("Account temporarily locked after repeated failures. Try again in 15 minutes.", statusCode: 429);

        if (o.BreakGlass.Verify(req.UserName, req.Password))
        {
            Lockouts.TryRemove(key, out _);
            return await SignInAsync(http, db, o.BreakGlass.User!, "Break-glass", null, null, Roles.SuperAdmin, "BREAK_GLASS", lf);
        }

        if (o.Ldap.Enabled && TryLdap(o.Ldap, req.UserName, req.Password) is { } info)
        {
            Lockouts.TryRemove(key, out _);
            var account = $"{o.Ldap.Domain}\\{info.Sam}";
            return await SignInAsync(http, db, account, info.DisplayName, info.Mail, info.Mobile, MapRole(info.Groups, o), "LDAP", lf);
        }

        var fails = Lockouts.AddOrUpdate(key, _ => (1, DateTimeOffset.MinValue),
            (_, v) => (v.Fails + 1, v.Fails + 1 >= MaxFails ? DateTimeOffset.UtcNow + LockFor : v.Until));
        db.AuditLog.Add(new AuditLog { UserName = req.UserName, ClientIp = http.Connection.RemoteIpAddress, Action = "LOGIN_FAILED" });
        await db.SaveChangesAsync();
        return Results.Problem(fails.Fails >= MaxFails ? "Account temporarily locked." : "Invalid user name or password.", statusCode: 401);
    }

    private static async Task<IResult> SignInAsync(HttpContext http, NmsDb db, string userName, string? displayName,
        string? email, string? mobile, string? mappedRole, string method, ILoggerFactory lf)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName);
        if (user is null && mappedRole is not null)
        {
            user = new AppUser { UserName = userName, DisplayName = displayName ?? userName, Role = mappedRole, Email = email, Mobile = mobile };
            db.Users.Add(user);
        }
        else if (user is not null && mappedRole is not null && user.Role != mappedRole && method != "BREAK_GLASS")
            user.Role = mappedRole;   // AD is the source of truth for roles

        if (user is null || !user.IsActive)
        {
            db.AuditLog.Add(new AuditLog { UserName = userName, ClientIp = http.Connection.RemoteIpAddress, Action = "LOGIN_DENIED" });
            await db.SaveChangesAsync();
            return Results.Problem("Your account is not authorized for CBH-NMS. Ask an administrator for access.", statusCode: 403);
        }

        if (displayName is not null) user.DisplayName = displayName;
        if (email is not null) user.Email = email;
        if (mobile is not null && user.Mobile is null) user.Mobile = mobile;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        db.AuditLog.Add(new AuditLog
        {
            UserId = user.UserId == 0 ? null : user.UserId, UserName = userName,
            ClientIp = http.Connection.RemoteIpAddress, Action = "LOGIN", NewValue = $"{{\"method\":\"{method}\"}}",
        });
        await db.SaveChangesAsync();

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        lf.CreateLogger("Auth").LogInformation("{User} signed in via {Method}", userName, method);
        return Results.Ok(new { userName, user.DisplayName, user.Role });
    }

    private static async Task<IResult> Me(HttpContext http, NmsDb db, BranchScope scope)
    {
        var id = http.User.UserId();
        var u = await db.Users.AsNoTracking().FirstAsync(x => x.UserId == id);
        var allowed = await scope.AllowedAsync(http.User);
        return Results.Ok(new
        {
            u.UserId, u.UserName, u.DisplayName, u.Role, u.Email, u.Mobile,
            scopedBranchIds = allowed,
            permissions = Permissions.For(u.Role),
        });
    }

    public static string? MapRole(IEnumerable<string> groups, AuthOptions o)
    {
        var set = groups.Select(g => g.ToLowerInvariant()).ToHashSet();
        var shortNames = set.Select(g => g.Contains('\\') ? g[(g.IndexOf('\\') + 1)..] : g).ToHashSet();
        return o.GroupRoleMap
            .Where(kv => set.Contains(kv.Key.ToLowerInvariant()) || shortNames.Contains(kv.Key.ToLowerInvariant()))
            .Select(kv => kv.Value).OrderBy(Permissions.RoleOrder).FirstOrDefault();
    }

    private sealed record LdapInfo(string Sam, string DisplayName, string? Mail, string? Mobile, List<string> Groups);

    private static LdapInfo? TryLdap(LdapOptions o, string userName, string password)
    {
        if (string.IsNullOrEmpty(password)) return null;   // anonymous bind would "succeed"
        var sam = userName.Contains('\\') ? userName[(userName.IndexOf('\\') + 1)..] : userName.Split('@')[0];
        try
        {
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(o.Server, o.Port));
            conn.SessionOptions.SecureSocketLayer = true;
            conn.SessionOptions.ProtocolVersion = 3;
            conn.AuthType = AuthType.Negotiate;
            conn.Bind(new NetworkCredential(sam, password, o.Domain));

            var search = new SearchRequest(o.BaseDn, $"(sAMAccountName={EscapeFilter(sam)})", SearchScope.Subtree,
                "displayName", "mail", "mobile", "memberOf");
            var res = (SearchResponse)conn.SendRequest(search);
            var e = res.Entries.Count > 0 ? res.Entries[0] : null;
            string? Attr(string n) => e?.Attributes[n] is { Count: > 0 } a ? a[0]?.ToString() : null;
            var groups = new List<string>();
            if (e?.Attributes["memberOf"] is { } mo)
                foreach (var v in mo.GetValues(typeof(string)))
                    if (v is string dn && dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                        groups.Add(dn[3..dn.IndexOf(',')]);
            return new LdapInfo(sam, Attr("displayName") ?? sam, Attr("mail"), Attr("mobile"), groups);
        }
        catch (LdapException) { return null; }
        catch (DirectoryOperationException) { return null; }
    }

    private static string EscapeFilter(string s) =>
        s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}

/// <summary>What each role may do (§9.4), sent to the UI so it can hide actions the API would refuse.</summary>
public static class Permissions
{
    public static int RoleOrder(string role) => Array.IndexOf(
        [Roles.SuperAdmin, Roles.HeadOfIt, Roles.ItManager, Roles.NetAdmin, Roles.BranchEngineer, Roles.Management], role) switch
    { -1 => 99, var i => i };

    public static string[] For(string role) => role switch
    {
        Roles.SuperAdmin => ["ack", "incident.update", "incident.close", "inventory.edit", "maintenance", "reports", "admin", "audit"],
        Roles.HeadOfIt => ["ack", "incident.update", "incident.close", "maintenance", "maintenance.approve", "reports", "audit"],
        Roles.ItManager => ["ack", "incident.update", "incident.close", "maintenance", "reports"],
        Roles.NetAdmin => ["ack", "incident.update", "inventory.edit", "reports"],
        Roles.BranchEngineer => ["ack", "incident.update"],
        Roles.Management => ["reports"],
        _ => [],
    };
}
