using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Cbh.Nms.Core.Agents;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Cbh.Nms.Core.Endpoints;

/// <summary>
/// "Authorization: Endpoint &lt;endpointId&gt;:&lt;key&gt;" for the Endpoint Agent on user PCs (D-14).
/// Same model as the Branch Agent: only the SHA-256 of the key is stored, sources limited to
/// <c>Agent:Auth:AllowedNetworks</c>.
/// </summary>
public sealed class EndpointAuthHandler(
    IOptionsMonitor<AgentAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder, NpgsqlDataSource ds)
    : AuthenticationHandler<AgentAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "Endpoint";
    public const string EndpointClaim = "nms:endpoint_id";
    public const string BranchClaim = "nms:endpoint_branch_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(SchemeName + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        if (!IsAllowedSource(Options.AllowedNetworks, Context.Connection.RemoteIpAddress))
            return AuthenticateResult.Fail("Source address not in agent networks");

        var cred = header[(SchemeName.Length + 1)..].Trim();
        var colon = cred.IndexOf(':');
        if (colon <= 0 || !int.TryParse(cred[..colon], out var endpointId))
            return AuthenticateResult.Fail("Malformed endpoint credential");

        await using var cmd = ds.CreateCommand("SELECT key_hash, branch_id FROM ep.endpoint WHERE endpoint_id = $1 AND NOT is_revoked");
        cmd.Parameters.AddWithValue(endpointId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return AuthenticateResult.Fail("Unknown or revoked endpoint");
        var stored = (byte[])r[0];
        var branchId = r.GetInt32(1);

        if (!CryptographicOperations.FixedTimeEquals(AgentAuthHandler.HashKey(cred[(colon + 1)..]), stored))
            return AuthenticateResult.Fail("Bad endpoint key");

        var identity = new ClaimsIdentity(
            [new Claim(EndpointClaim, endpointId.ToString()), new Claim(BranchClaim, branchId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static bool IsAllowedSource(List<string> nets, IPAddress? ip)
    {
        if (nets.Count == 0) return true;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return nets.Any(n => IPNetwork.Parse(n).Contains(ip));
    }
}

public static class EndpointPrincipal
{
    public static int EndpointId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue(EndpointAuthHandler.EndpointClaim)!);
}
