using System.Net;
using System.Text.Json;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using Cbh.Nms.Shared;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Cbh.Nms.Core.Endpoints;

/// <summary>/endpoint/v1 â€” the Endpoint Agent's API (D-14). Uploads are idempotent per BatchId.</summary>
public static class EndpointAgentApi
{
    public const int MaxUploadsPerCall = 120;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapEndpointAgentApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/endpoint/v1");
        g.MapPost("/register", Register).AllowAnonymous();
        var authed = g.MapGroup("").RequireAuthorization(AuthPolicies.Endpoint);
        authed.MapGet("/config", GetConfig);
        authed.MapPost("/reports", PostReports);
        authed.MapPost("/inventory", PostInventory);
        authed.MapPost("/activity", PostActivity);
    }

    private static async Task<IResult> Register(EndpointRegisterRequest req, HttpContext http, NpgsqlDataSource ds, NmsDb db,
        IOptionsMonitor<AgentAuthOptions> auth, ILoggerFactory lf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EnrollKey) || string.IsNullOrWhiteSpace(req.HostName) || string.IsNullOrWhiteSpace(req.MachineGuid))
            return Results.Problem("EnrollKey, HostName and MachineGuid are required", statusCode: 400);
        if (!EndpointAuthHandler.IsAllowedSource(auth.Get(EndpointAuthHandler.SchemeName).AllowedNetworks, http.Connection.RemoteIpAddress))
            return Results.Problem("Source address not in agent networks", statusCode: 403);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int keyId, branchId;
        await using (var cmd = new NpgsqlCommand("""
            SELECT enroll_key_id, branch_id FROM ep.enroll_key
            WHERE key_hash = $1 AND NOT is_revoked AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses)
            FOR UPDATE
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(AgentAuthHandler.HashKey(req.EnrollKey.Trim()));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.Problem("Enrollment key invalid, revoked, used up or expired", statusCode: 401);
            keyId = r.GetInt32(0); branchId = r.GetInt32(1);
        }

        var secret = AgentAuthHandler.NewSecret();
        int endpointId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO ep.endpoint (branch_id, machine_guid, host_name, key_hash, agent_version)
            VALUES (@b, @g, @h, @k, @v)
            ON CONFLICT (machine_guid) DO UPDATE
               SET branch_id = excluded.branch_id, host_name = excluded.host_name, key_hash = excluded.key_hash,
                   agent_version = excluded.agent_version, is_revoked = false
            RETURNING endpoint_id;
            UPDATE ep.enroll_key SET uses = uses + 1 WHERE enroll_key_id = @key;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("b", branchId);
            cmd.Parameters.AddWithValue("g", Cut(req.MachineGuid.Trim(), 64)!);
            cmd.Parameters.AddWithValue("h", Cut(req.HostName.Trim(), 100)!);
            cmd.Parameters.AddWithValue("k", AgentAuthHandler.HashKey(secret));
            cmd.Parameters.AddWithValue("v", (object?)Cut(req.AgentVersion, 20) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("key", keyId);
            endpointId = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);

        db.AuditLog.Add(new AuditLog
        {
            UserName = "endpoint:" + req.HostName, Action = "ENDPOINT_REGISTER", EntityType = "endpoint",
            EntityId = endpointId.ToString(), ClientIp = http.Connection.RemoteIpAddress,
            NewValue = JsonSerializer.Serialize(new { branchId, req.HostName, req.AgentVersion, enrollKeyId = keyId }),
        });
        await db.SaveChangesAsync(ct);
        lf.CreateLogger("Endpoints").LogInformation("Endpoint {Id} ({Host}) registered for branch {Branch}", endpointId, req.HostName, branchId);
        return Results.Ok(new EndpointRegisterResponse(endpointId, secret, branchId));
    }

    private static async Task<IResult> GetConfig(NpgsqlDataSource ds, IConfiguration cfg, CancellationToken ct)
    {
        var activity = await EndpointSettings.LoadActivityAsync(ds, ct);
        return Results.Ok(new EndpointConfig(
            cfg.GetValue("Endpoint:ReportIntervalS", 60),
            cfg.GetValue("Endpoint:InventoryIntervalMin", 360),
            activity.Enabled,
            cfg["Agent:HoProbeTarget"]));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€ reports: health + network every minute â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task<IResult> PostReports(List<EndpointUpload<EndpointReport>> uploads, HttpContext http,
        NpgsqlDataSource ds, CancellationToken ct)
    {
        if (uploads.Count > MaxUploadsPerCall) return Results.Problem($"At most {MaxUploadsPerCall} uploads per call", statusCode: 413);
        var id = http.User.EndpointId();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var times = new List<DateTimeOffset>(); var keys = new List<string>(); var values = new List<double>();
        EndpointReport? newest = null;
        foreach (var u in uploads)
        {
            if (!await ClaimAsync(conn, tx, id, u.BatchId, ct)) continue;
            var r = u.Body;
            var t = r.Time.ToUniversalTime();
            void Add(string k, double? v) { if (v is { } x && double.IsFinite(x)) { times.Add(t); keys.Add(k); values.Add(x); } }
            Add(EndpointMetricKeys.CpuPct, r.Health.CpuPct);
            Add(EndpointMetricKeys.MemPct, r.Health.MemPct);
            var sys = r.Health.Disks.FirstOrDefault(d => d.IsSystem) ?? r.Health.Disks.FirstOrDefault();
            if (sys is { TotalGb: > 0 }) Add(EndpointMetricKeys.DiskFreePct, 100 * sys.FreeGb / sys.TotalGb);
            Add(EndpointMetricKeys.GatewayRttMs, r.Network.GatewayRttMs);
            Add(EndpointMetricKeys.GatewayLossPct, r.Network.GatewayLossPct);
            Add(EndpointMetricKeys.HoRttMs, r.Network.HoRttMs);
            Add(EndpointMetricKeys.HoLossPct, r.Network.HoLossPct);
            Add(EndpointMetricKeys.RxKbps, r.Network.RxKbps);
            Add(EndpointMetricKeys.TxKbps, r.Network.TxKbps);
            if (newest is null || r.Time > newest.Time) newest = r;
        }

        if (times.Count > 0)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO ep.metric (time, endpoint_id, metric_key, value)
                SELECT t, $1, k, v FROM unnest($2::timestamptz[], $3::text[], $4::float8[]) AS x(t, k, v)
                ON CONFLICT DO NOTHING
                """, conn, tx);
            ins.Parameters.AddWithValue(id);
            ins.Parameters.AddWithValue(times.ToArray());
            ins.Parameters.AddWithValue(keys.ToArray());
            ins.Parameters.AddWithValue(values.ToArray());
            await ins.ExecuteNonQueryAsync(ct);
        }

        await using (var upd = new NpgsqlCommand(newest is null
            ? "UPDATE ep.endpoint SET last_seen_at = now() WHERE endpoint_id = @id"
            : """
              UPDATE ep.endpoint SET last_seen_at = now(), last_user = @user, ip_address = @ip, mac_address = @mac,
                     nics = @nics::jsonb, live = @live::jsonb
              WHERE endpoint_id = @id AND (live IS NULL OR (live->>'time')::timestamptz <= @time)
              """, conn, tx))
        {
            upd.Parameters.AddWithValue("id", id);
            if (newest is not null)
            {
                upd.Parameters.AddWithValue("user", (object?)Cut(newest.LoggedOnUser, 100) ?? DBNull.Value);
                upd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet)
                    { Value = IPAddress.TryParse(newest.Network.PrimaryIp, out var ip) ? ip : DBNull.Value });
                upd.Parameters.AddWithValue("mac", (object?)Cut(newest.Network.Mac, 20) ?? DBNull.Value);
                upd.Parameters.AddWithValue("nics", ToJson(newest.Network.Nics));
                upd.Parameters.AddWithValue("live", ToJson(new
                {
                    time = newest.Time.ToUniversalTime(), newest.Health, network = newest.Network with { Nics = [] },
                }));
                upd.Parameters.AddWithValue("time", newest.Time.ToUniversalTime());
            }
            await upd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { accepted = uploads.Count });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€ inventory: hardware, security, installed software â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task<IResult> PostInventory(EndpointUpload<EndpointInventory> upload, HttpContext http,
        NpgsqlDataSource ds, CancellationToken ct)
    {
        var id = http.User.EndpointId();
        var inv = upload.Body;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (!await ClaimAsync(conn, tx, id, upload.BatchId, ct)) return Results.Ok(new { duplicate = true });

        var h = inv.Hardware;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE ep.endpoint SET host_name = @host, domain = @domain, os_name = @os, os_version = @osv, os_build = @osb,
                   manufacturer = @mf, model = @model, serial_no = @sn, cpu_model = @cpu, cpu_cores = @cores, ram_mb = @ram,
                   last_boot_at = @boot, security = @sec::jsonb, inventory_at = now(), last_seen_at = now()
            WHERE endpoint_id = @id
            """, conn, tx))
        {
            object N(object? v) => v ?? DBNull.Value;
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("host", Cut(h.HostName, 100) ?? "");
            cmd.Parameters.AddWithValue("domain", N(Cut(h.Domain, 100)));
            cmd.Parameters.AddWithValue("os", N(Cut(h.OsName, 100)));
            cmd.Parameters.AddWithValue("osv", N(Cut(h.OsVersion, 50)));
            cmd.Parameters.AddWithValue("osb", N(Cut(h.OsBuild, 20)));
            cmd.Parameters.AddWithValue("mf", N(Cut(h.Manufacturer, 100)));
            cmd.Parameters.AddWithValue("model", N(Cut(h.Model, 100)));
            cmd.Parameters.AddWithValue("sn", N(Cut(h.SerialNo, 100)));
            cmd.Parameters.AddWithValue("cpu", N(Cut(h.CpuModel, 150)));
            cmd.Parameters.Add(new NpgsqlParameter("cores", NpgsqlDbType.Smallint) { Value = h.CpuCores is { } c ? (short)c : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ram", NpgsqlDbType.Integer) { Value = h.RamMb is { } m ? (int)m : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("boot", NpgsqlDbType.TimestampTz) { Value = h.LastBootAt?.ToUniversalTime() is { } b ? b : DBNull.Value });
            cmd.Parameters.AddWithValue("sec", ToJson(inv.Security));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Software: diff against what we knew, record the changes, replace the list.
        var before = new List<InstalledSoftware>();
        await using (var cmd = new NpgsqlCommand("SELECT name, version FROM ep.software WHERE endpoint_id = $1", conn, tx))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) before.Add(new(r.GetString(0), r.GetString(1)));
        }
        var items = inv.Software
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s with { Name = Cut(s.Name.Trim(), 300)!, Version = Cut(s.Version?.Trim() ?? "", 100) })
            .DistinctBy(s => (s.Name.ToLowerInvariant(), s.Version!.ToLowerInvariant()))
            .ToList();
        var changes = SoftwareDiff.Compute(before, items.Select(s => new InstalledSoftware(s.Name, s.Version!)).ToList());

        await using (var cmd = new NpgsqlCommand("""
            DELETE FROM ep.software s WHERE s.endpoint_id = @id
              AND NOT EXISTS (SELECT 1 FROM unnest(@n::text[], @v::text[]) AS n(name, version)
                              WHERE n.name = s.name AND n.version = s.version);
            INSERT INTO ep.software (endpoint_id, name, version, publisher, install_date)
            SELECT @id, n.name, n.version, n.publisher, n.install_date
            FROM unnest(@n::text[], @v::text[], @p::text[], @d::date[]) AS n(name, version, publisher, install_date)
            ON CONFLICT (endpoint_id, name, version) DO UPDATE
               SET last_seen = now(), publisher = excluded.publisher, install_date = excluded.install_date;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", items.Select(s => s.Name).ToArray());
            cmd.Parameters.AddWithValue("v", items.Select(s => s.Version!).ToArray());
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "p", NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                Value = items.Select(s => (object?)Cut(s.Publisher, 200) ?? DBNull.Value).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { ParameterName = "d", NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Date,
                Value = items.Select(s => s.InstallDate is { } d ? (object)d : DBNull.Value).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (changes.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO ep.software_event (endpoint_id, action, name, version, old_version)
                SELECT $1, a, n, v, o FROM unnest($2::text[], $3::text[], $4::text[], $5::text[]) AS x(a, n, v, o)
                """, conn, tx);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(changes.Select(c => c.Action).ToArray());
            cmd.Parameters.AddWithValue(changes.Select(c => c.Name).ToArray());
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                Value = changes.Select(c => (object?)c.Version ?? DBNull.Value).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                Value = changes.Select(c => (object?)c.OldVersion ?? DBNull.Value).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { software = items.Count, changes = changes.Count });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€ activity: app usage + session events (only when enabled by policy) â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static async Task<IResult> PostActivity(List<EndpointUpload<ActivityBatch>> uploads, HttpContext http,
        NpgsqlDataSource ds, CancellationToken ct)
    {
        if (uploads.Count > MaxUploadsPerCall) return Results.Problem($"At most {MaxUploadsPerCall} uploads per call", statusCode: 413);
        // Policy switched off after the agent collected: discard rather than store.
        if (!(await EndpointSettings.LoadActivityAsync(ds, ct)).Enabled) return Results.Ok(new { accepted = 0, discarded = uploads.Count });

        var id = http.User.EndpointId();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var u in uploads)
        {
            if (!await ClaimAsync(conn, tx, id, u.BatchId, ct)) continue;
            var usage = u.Body.Usage.Where(x => x.ActiveSeconds > 0 && !string.IsNullOrWhiteSpace(x.App) && !string.IsNullOrWhiteSpace(x.User))
                .GroupBy(x => (x.Day, User: Cut(x.User, 100)!, App: Cut(x.App.ToLowerInvariant(), 260)!))
                .Select(g => (g.Key.Day, g.Key.User, g.Key.App, Product: Cut(g.Select(x => x.Product).FirstOrDefault(p => p != null), 200),
                              Secs: Math.Min(g.Sum(x => x.ActiveSeconds), 86400)))
                .ToList();
            if (usage.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO ep.app_usage (endpoint_id, day, user_name, app, product, active_s)
                    SELECT $1, d, u, a, p, s FROM unnest($2::date[], $3::text[], $4::text[], $5::text[], $6::int[]) AS x(d, u, a, p, s)
                    ON CONFLICT (endpoint_id, day, user_name, app) DO UPDATE
                       SET active_s = least(ep.app_usage.active_s + excluded.active_s, 86400),
                           product = coalesce(excluded.product, ep.app_usage.product)
                    """, conn, tx);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(usage.Select(x => x.Day).ToArray());
                cmd.Parameters.AddWithValue(usage.Select(x => x.User).ToArray());
                cmd.Parameters.AddWithValue(usage.Select(x => x.App).ToArray());
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                    Value = usage.Select(x => (object?)x.Product ?? DBNull.Value).ToArray() });
                cmd.Parameters.AddWithValue(usage.Select(x => x.Secs).ToArray());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var events = u.Body.Events.Where(e => e.Event is SessionEvents.Logon or SessionEvents.Logoff or SessionEvents.Lock
                or SessionEvents.Unlock or SessionEvents.Idle or SessionEvents.Active && !string.IsNullOrWhiteSpace(e.User)).ToList();
            if (events.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO ep.session_event (time, endpoint_id, user_name, event)
                    SELECT t, $1, u, e FROM unnest($2::timestamptz[], $3::text[], $4::text[]) AS x(t, u, e)
                    """, conn, tx);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(events.Select(e => e.Time.ToUniversalTime()).ToArray());
                cmd.Parameters.AddWithValue(events.Select(e => Cut(e.User, 100)!).ToArray());
                cmd.Parameters.AddWithValue(events.Select(e => e.Event).ToArray());
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { accepted = uploads.Count });
    }

    /// <summary>True the first time a batch id is seen for this endpoint.</summary>
    private static async Task<bool> ClaimAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int endpointId, Guid batchId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO ep.ingest_batch (endpoint_id, batch_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn, tx);
        cmd.Parameters.AddWithValue(endpointId); cmd.Parameters.AddWithValue(batchId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Trims to the column size and drops NUL characters, which PostgreSQL text cannot hold
    /// (some registry values end in \0).</summary>
    internal static string? Cut(string? s, int max)
    {
        if (s is null) return null;
        if (s.Contains('\0')) s = s.Replace("\0", "");
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>JSON for a jsonb column: PostgreSQL rejects \u0000 there as well.</summary>
    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json).Replace("\\u0000", "");
}
