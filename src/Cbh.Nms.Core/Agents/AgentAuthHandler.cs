using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Cbh.Nms.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Core.Agents;

public sealed class AgentAuthOptions : AuthenticationSchemeOptions
{
    /// <summary>CIDRs agents may connect from (the MPLS ranges). Empty = any (development only).</summary>
    public List<string> AllowedNetworks { get; set; } = [];
}

/// <summary>
/// "Authorization: Agent &lt;agentId&gt;:&lt;key&gt;" (SRS §7.1, NFR-09). Only the SHA-256 of the key is
/// stored at HO; revoking one agent leaves the others untouched.
/// </summary>
public sealed class AgentAuthHandler(
    IOptionsMonitor<AgentAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder, NmsDb db)
    : AuthenticationHandler<AgentAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "Agent";
    public const string BranchClaim = "nms:branch_id";
    public const string AgentClaim = "nms:agent_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        if (!IsAllowedSource(Context.Connection.RemoteIpAddress))
            return AuthenticateResult.Fail("Source address not in agent networks");

        var cred = header[(SchemeName.Length + 1)..].Trim();
        var colon = cred.IndexOf(':');
        if (colon <= 0 || !int.TryParse(cred[..colon], out var agentId))
            return AuthenticateResult.Fail("Malformed agent credential");

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.AgentId == agentId);
        if (agent is null || agent.IsRevoked) return AuthenticateResult.Fail("Unknown or revoked agent");

        var hash = HashKey(cred[(colon + 1)..]);
        if (!CryptographicOperations.FixedTimeEquals(hash, agent.KeyHash))
            return AuthenticateResult.Fail("Bad agent key");

        var identity = new ClaimsIdentity(
            [new Claim(AgentClaim, agentId.ToString()), new Claim(BranchClaim, agent.BranchId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private bool IsAllowedSource(IPAddress? ip)
    {
        var nets = Options.AllowedNetworks;
        if (nets.Count == 0) return true;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return nets.Any(n => IPNetwork.Parse(n).Contains(ip));
    }

    public static byte[] HashKey(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class AgentPrincipal
{
    public static int AgentId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(AgentAuthHandler.AgentClaim)!);
    public static int BranchId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(AgentAuthHandler.BranchClaim)!);
}
