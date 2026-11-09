using System.Text.Json;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cbh.Nms.Core.Agents;

/// <summary>/agent/v1 — the Branch Agent's API (SRS §7.2). Kept stable for at least a year per version.</summary>
public static class AgentEndpoints
{
    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/agent/v1");
        g.MapPost("/register", Register).AllowAnonymous();

        var authed = g.MapGroup("").RequireAuthorization(AuthPolicies.Agent);
        authed.MapGet("/config", GetConfig);
        authed.MapPost("/metrics", PostMetrics);
        authed.MapPost("/heartbeat", PostHeartbeat);
    }

    private static async Task<IResult> Register(RegisterRequest req, NmsDb db, ILoggerFactory lf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EnrollToken) || string.IsNullOrWhiteSpace(req.HostName))
            return Results.Problem("EnrollToken and HostName are required", statusCode: 400);

        var tokenHash = AgentAuthHandler.HashKey(req.EnrollToken);
        var token = await db.EnrollTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (token is null || token.UsedAt is not null || token.ExpiresAt < DateTimeOffset.UtcNow)
            return Results.Problem("Enrollment token invalid, used or expired", statusCode: 401);

        var key = AgentAuthHandler.NewSecret();
        var agent = new Agent
        {
            BranchId = token.BranchId,
            HostName = req.HostName.Trim(),
            AgentVersion = req.AgentVersion,
            KeyHash = AgentAuthHandler.HashKey(key),
        };
        token.UsedAt = DateTimeOffset.UtcNow;
        db.Agents.Add(agent);
        db.AuditLog.Add(new AuditLog
        {
            UserName = "agent:" + agent.HostName, Action = "AGENT_REGISTER", EntityType = "agent",
            NewValue = JsonSerializer.Serialize(new { agent.BranchId, agent.HostName, agent.AgentVersion }),
        });
        await db.SaveChangesAsync(ct);

        lf.CreateLogger("Agents").LogInformation("Agent {AgentId} registered for branch {BranchId} from {Host}",
            agent.AgentId, agent.BranchId, agent.HostName);
        return Results.Ok(new RegisterResponse(agent.AgentId, key, agent.BranchId));
    }

    private static async Task<IResult> GetConfig(HttpContext http, NmsDb db, IDataProtectionProvider dp,
        IConfiguration cfg, CancellationToken ct)
    {
        var branchId = http.User.BranchId();
        var version = await db.BranchConfigVersions.Where(v => v.BranchId == branchId)
            .Select(v => (int?)v.Version).FirstOrDefaultAsync(ct) ?? 0;

        var etag = $"\"v{version}\"";
        if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);

        var protector = dp.CreateProtector(SnmpSecrets.Purpose);
        var links = await db.IspLinks.AsNoTracking()
            .Where(l => l.BranchId == branchId && l.IsActive && l.ProbeTarget != null)
            .Select(l => new IspLinkProbe(l.IspLinkId, l.LinkRole, l.ProbeTarget!.ToString()))
            .ToListAsync(ct);

        var devices = await db.Devices.AsNoTracking().Include(d => d.DeviceType).Include(d => d.SnmpProfile)
            .Where(d => d.BranchId == branchId && d.IsActive).ToListAsync(ct);

        var checks = await db.AppChecks.AsNoTracking()
            .Where(c => c.IsActive && (c.BranchId == branchId || c.BranchId == null))
            .Select(c => new AppCheckTarget(c.AppCheckId, c.CheckType, c.Target, c.ExpectedStatus, c.TimeoutMs))
            .ToListAsync(ct);

        var config = new AgentConfig(
            branchId, version,
            PollIntervalSeconds: cfg.GetValue("Agent:PollIntervalSeconds", 60),
            HoProbeTarget: cfg["Agent:HoProbeTarget"],
            IspLinks: links,
            Devices: devices.Select(d => new DeviceTarget(
                d.DeviceId, d.DeviceType!.Name, d.Vendor, d.IpAddress.ToString(), d.PollIntervalS,
                d.SnmpProfile is { } p
                    ? new SnmpCredential(p.SnmpUser, p.AuthProtocol, protector.Unprotect(p.AuthSecretEnc),
                        p.PrivProtocol, protector.Unprotect(p.PrivSecretEnc))
                    : null,
                d.SnmpProfile is { } p2
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(p2.Oids) ?? []
                    : new Dictionary<string, string>())).ToList(),
            AppChecks: checks);

        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(config);
    }

    private static async Task<IResult> PostMetrics(MetricBatch batch, HttpContext http, MetricIngestor ingestor,
        CancellationToken ct)
    {
        var agentId = http.User.AgentId();
        if (batch.AgentId != agentId) return Results.Problem("agentId does not match credential", statusCode: 403);
        if (batch.Items is not { Count: > 0 }) return Results.Ok(new MetricAck(0));
        if (batch.Items.Count > AgentLimits.MaxBatchRows)
            return Results.Problem($"Batch exceeds {AgentLimits.MaxBatchRows} rows", statusCode: 413);

        await ingestor.IngestAsync(agentId, http.User.BranchId(), batch.Items, ct);
        return Results.Ok(new MetricAck(batch.Items.Max(i => i.Seq)));
    }

    private static async Task<IResult> PostHeartbeat(Heartbeat hb, HttpContext http, NmsDb db, CancellationToken ct)
    {
        var agentId = http.User.AgentId();
        await db.Agents.Where(a => a.AgentId == agentId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.LastSeenAt, DateTimeOffset.UtcNow)
            .SetProperty(a => a.AgentVersion, hb.AgentVersion)
            .SetProperty(a => a.ConfigVersion, hb.ConfigVersion)
            .SetProperty(a => a.BufferRows, hb.BufferRows), ct);
        return Results.NoContent();
    }
}

public static class SnmpSecrets
{
    public const string Purpose = "Cbh.Nms.SnmpSecrets.v1";
}
