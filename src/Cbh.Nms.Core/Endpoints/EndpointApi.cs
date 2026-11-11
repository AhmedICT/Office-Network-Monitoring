using System.Net;
using System.Text.Json;
using Cbh.Nms.Core.Agents;
using Cbh.Nms.Core.Data;
using Npgsql;
using NpgsqlTypes;

namespace Cbh.Nms.Core.Endpoints;

public sealed record EnrollKeyRequest(int BranchId, string? Label, int? ValidDays, int? MaxUses);
public sealed record SoftwareRuleInput(string Kind, string Pattern, string Severity, string? Note, bool IsEnabled = true);
public sealed record ActivitySettingsInput(bool Enabled, int RetentionDays, bool NoticeConfirmed);

/// <summary>/api/v1 for the "User PCs" screens (D-14). Branch scope applies everywhere; activity data is
/// limited to Super Admin / Head of IT and every view of it is audited.</summary>
public static class EndpointApi
{
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(3);

    public static void MapEndpointApi(this IEndpointRouteBuilder api)
    {
        var view = api.MapGroup("").RequireAuthorization(AuthPolicies.Viewer);
        view.MapGet("/endpoints", List);
        view.MapGet("/endpoints/{id:int}", Get);
        view.MapGet("/endpoints/{id:int}/metrics", Metrics);
        view.MapGet("/endpoints/{id:int}/software", Software);
        view.MapGet("/endpoints/{id:int}/software-events", SoftwareEvents);
        view.MapGet("/software", FleetSoftware);
        view.MapGet("/software-rules", ListRules);

        api.MapGet("/endpoints/{id:int}/activity", Activity).RequireAuthorization(AuthPolicies.ActivityView);

        var edit = api.MapGroup("").RequireAuthorization(AuthPolicies.EditInventory);
        edit.MapDelete("/endpoints/{id:int}", Revoke);
        edit.MapGet("/endpoint-enroll-keys", ListKeys);
        edit.MapPost("/endpoint-enroll-keys", CreateKey);
        edit.MapDelete("/endpoint-enroll-keys/{id:int}", RevokeKey);
        edit.MapPost("/software-rules", CreateRule);
        edit.MapPut("/software-rules/{id:int}", UpdateRule);
        edit.MapDelete("/software-rules/{id:int}", DeleteRule);

        api.MapGet("/endpoint-settings", async (NpgsqlDataSource ds, CancellationToken ct) =>
            Results.Ok(await EndpointSettings.LoadActivityAsync(ds, ct))).RequireAuthorization(AuthPolicies.Viewer);
        api.MapPut("/endpoint-settings", SaveSettings).RequireAuthorization(AuthPolicies.Admin);
    }

    // ───────── list / detail ─────────

    private static async Task<IResult> List(int? branchId, string? q, string? status, HttpContext http, BranchScope scope,
        NpgsqlDataSource ds, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        await using var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.host_name, b.branch_id, b.branch_code, e.last_user, host(e.ip_address), e.os_name,
                   e.model, e.last_seen_at, e.last_seen_at > now() - $5::interval AS online,
                   (e.live->'health'->>'cpuPct')::float, (e.live->'health'->>'memPct')::float,
                   (SELECT min(100 * (d->>'freeGb')::float / nullif((d->>'totalGb')::float, 0))
                      FROM jsonb_array_elements(coalesce(e.live->'health'->'disks', '[]')) d),
                   (SELECT count(*) FROM ops.alert a WHERE a.entity_type = 'ENDPOINT' AND a.entity_id = e.endpoint_id
                       AND a.cleared_at IS NULL AND NOT a.is_suppressed),
                   e.agent_version, (e.live->'network'->>'hoRttMs')::float
            FROM ep.endpoint e JOIN org.branch b ON b.branch_id = e.branch_id
            WHERE NOT e.is_revoked
              AND ($1::int[] IS NULL OR e.branch_id = ANY($1))
              AND ($2::int IS NULL OR e.branch_id = $2)
              AND ($3::text IS NULL OR e.host_name ILIKE '%' || $3 || '%' OR e.last_user ILIKE '%' || $3 || '%'
                   OR host(e.ip_address) LIKE $3 || '%' OR e.serial_no ILIKE $3)
              AND ($4::text IS NULL OR ($4 = 'online') = (e.last_seen_at > now() - $5::interval))
            ORDER BY b.branch_code, e.host_name
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = allowed is null ? DBNull.Value : allowed.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)branchId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim(), NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = status is "online" or "offline" ? status : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(OnlineWithin);

        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            double? D(int i) => r.IsDBNull(i) ? null : Math.Round(r.GetDouble(i), 1);
            items.Add(new
            {
                endpointId = r.GetInt32(0), hostName = r.GetString(1), branchId = r.GetInt32(2), branchCode = r.GetString(3),
                lastUser = r.IsDBNull(4) ? null : r.GetString(4), ip = r.IsDBNull(5) ? null : r.GetString(5),
                os = r.IsDBNull(6) ? null : r.GetString(6), model = r.IsDBNull(7) ? null : r.GetString(7),
                lastSeenAt = r.IsDBNull(8) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(8),
                online = !r.IsDBNull(9) && r.GetBoolean(9),
                cpuPct = D(10), memPct = D(11), diskFreeMinPct = D(12), activeAlerts = r.GetInt64(13),
                agentVersion = r.IsDBNull(14) ? null : r.GetString(14), hoRttMs = D(15),
            });
        }
        return Results.Ok(new { items });
    }

    private static async Task<IResult> Get(int id, HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.branch_id, b.branch_code, b.branch_name, e.host_name, e.domain, e.os_name, e.os_version, e.os_build,
                   e.manufacturer, e.model, e.serial_no, e.cpu_model, e.cpu_cores, e.ram_mb, e.last_boot_at, host(e.ip_address),
                   e.mac_address, e.last_user, e.security::text, e.nics::text, e.live::text, e.last_seen_at, e.inventory_at,
                   e.registered_at, e.agent_version, e.last_seen_at > now() - $2::interval
            FROM ep.endpoint e JOIN org.branch b ON b.branch_id = e.branch_id
            WHERE e.endpoint_id = $1 AND NOT e.is_revoked
            """);
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(OnlineWithin);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.NotFound();
        var branchId = r.GetInt32(1);
        if (!await scope.CanSeeAsync(http.User, branchId, ct)) return Results.NotFound();

        string? S(int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
        JsonElement? J(int i) => r.IsDBNull(i) ? null : JsonDocument.Parse(r.GetString(i)).RootElement.Clone();
        DateTimeOffset? T(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return Results.Ok(new
        {
            endpointId = r.GetInt32(0), branchId, branchCode = r.GetString(2), branchName = r.GetString(3), hostName = r.GetString(4),
            domain = S(5), osName = S(6), osVersion = S(7), osBuild = S(8), manufacturer = S(9), model = S(10), serialNo = S(11),
            cpuModel = S(12), cpuCores = r.IsDBNull(13) ? (short?)null : r.GetInt16(13), ramMb = r.IsDBNull(14) ? (int?)null : r.GetInt32(14),
            lastBootAt = T(15), ip = S(16), mac = S(17), lastUser = S(18), security = J(19), nics = J(20), live = J(21),
            lastSeenAt = T(22), inventoryAt = T(23), registeredAt = T(24), agentVersion = S(25), online = !r.IsDBNull(26) && r.GetBoolean(26),
        });
    }

    /// <summary>?key=cpu_pct&amp;from&amp;to — 5-minute buckets up to 2 days, hourly beyond (max 30 days).</summary>
    private static async Task<IResult> Metrics(int id, string key, DateTimeOffset? from, DateTimeOffset? to, HttpContext http,
        BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (await BranchOf(ds, id, ct) is not { } b || !await scope.CanSeeAsync(http.User, b, ct)) return Results.NotFound();
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end.AddHours(-24)).ToUniversalTime();
        if (start >= end || end - start > TimeSpan.FromDays(30)) return Results.Problem("range must be 0–30 days", statusCode: 400);
        var bucket = end - start <= TimeSpan.FromDays(2) ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1);

        await using var cmd = ds.CreateCommand("""
            SELECT time_bucket($5, time) AS t, avg(value), max(value) FROM ep.metric
            WHERE endpoint_id = $1 AND metric_key = $2 AND time >= $3 AND time < $4
            GROUP BY t ORDER BY t
            """);
        cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(start); cmd.Parameters.AddWithValue(end); cmd.Parameters.AddWithValue(bucket);
        var points = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            points.Add(new { t = r.GetFieldValue<DateTimeOffset>(0), avg = Math.Round(r.GetDouble(1), 2), max = Math.Round(r.GetDouble(2), 2) });
        return Results.Ok(new { key, points });
    }

    private static async Task<IResult> Software(int id, HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (await BranchOf(ds, id, ct) is not { } b || !await scope.CanSeeAsync(http.User, b, ct)) return Results.NotFound();
        await using var cmd = ds.CreateCommand("""
            SELECT s.name, nullif(s.version, ''), s.publisher, s.install_date, s.first_seen,
                   (SELECT string_agg(DISTINCT r.kind, ',') FROM ep.software_rule r WHERE r.is_enabled AND s.name ILIKE r.like_pattern)
            FROM ep.software s WHERE s.endpoint_id = $1 ORDER BY lower(s.name)
            """);
        cmd.Parameters.AddWithValue(id);
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                name = r.GetString(0), version = r.IsDBNull(1) ? null : r.GetString(1), publisher = r.IsDBNull(2) ? null : r.GetString(2),
                installDate = r.IsDBNull(3) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(3), firstSeen = r.GetFieldValue<DateTimeOffset>(4),
                flags = r.IsDBNull(5) ? null : r.GetString(5),
            });
        return Results.Ok(new { items });
    }

    private static async Task<IResult> SoftwareEvents(int id, HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (await BranchOf(ds, id, ct) is not { } b || !await scope.CanSeeAsync(http.User, b, ct)) return Results.NotFound();
        await using var cmd = ds.CreateCommand(
            "SELECT time, action, name, version, old_version FROM ep.software_event WHERE endpoint_id = $1 ORDER BY time DESC LIMIT 200");
        cmd.Parameters.AddWithValue(id);
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new { time = r.GetFieldValue<DateTimeOffset>(0), action = r.GetString(1), name = r.GetString(2),
                            version = r.IsDBNull(3) ? null : r.GetString(3), oldVersion = r.IsDBNull(4) ? null : r.GetString(4) });
        return Results.Ok(new { items });
    }

    /// <summary>"Which PCs have X?" — software across all PCs in scope, with counts.</summary>
    private static async Task<IResult> FleetSoftware(string? q, HttpContext http, BranchScope scope, NpgsqlDataSource ds, CancellationToken ct)
    {
        var allowed = await scope.AllowedAsync(http.User, ct);
        await using var cmd = ds.CreateCommand("""
            SELECT s.name, nullif(s.version, ''), count(*) AS pcs,
                   (array_agg(e.host_name ORDER BY e.host_name))[1:20]
            FROM ep.software s JOIN ep.endpoint e ON e.endpoint_id = s.endpoint_id AND NOT e.is_revoked
            WHERE ($1::int[] IS NULL OR e.branch_id = ANY($1)) AND ($2::text IS NULL OR s.name ILIKE '%' || $2 || '%')
            GROUP BY s.name, s.version ORDER BY count(*) DESC, s.name LIMIT 500
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = allowed is null ? DBNull.Value : allowed.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim(), NpgsqlDbType = NpgsqlDbType.Text });
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new { name = r.GetString(0), version = r.IsDBNull(1) ? null : r.GetString(1), pcs = r.GetInt64(2), hosts = r.GetFieldValue<string[]>(3) });
        return Results.Ok(new { items });
    }

    // ───────── activity (restricted + audited) ─────────

    private static async Task<IResult> Activity(int id, DateOnly? day, int? days, HttpContext http, BranchScope scope,
        NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (await BranchOf(ds, id, ct) is not { } b || !await scope.CanSeeAsync(http.User, b, ct)) return Results.NotFound();
        var to = day ?? DateOnly.FromDateTime(DateTime.Now);
        var span = Math.Clamp(days ?? 1, 1, 31);
        var from = to.AddDays(-(span - 1));

        db.Record(http, "ENDPOINT_ACTIVITY_VIEW", "endpoint", id, newValue: new { from, to });
        await db.SaveChangesAsync(ct);

        var usage = new List<object>();
        await using (var cmd = ds.CreateCommand("""
            SELECT user_name, app, max(product), sum(active_s)::int, count(DISTINCT day)
            FROM ep.app_usage WHERE endpoint_id = $1 AND day BETWEEN $2 AND $3
            GROUP BY user_name, app ORDER BY user_name, sum(active_s) DESC
            """))
        {
            cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(from); cmd.Parameters.AddWithValue(to);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                usage.Add(new { user = r.GetString(0), app = r.GetString(1), product = r.IsDBNull(2) ? null : r.GetString(2),
                                activeSeconds = r.GetInt32(3), days = r.GetInt64(4) });
        }
        var events = new List<object>();
        await using (var cmd = ds.CreateCommand("""
            SELECT time, user_name, event FROM ep.session_event
            WHERE endpoint_id = $1 AND time >= $2::date::timestamp AT TIME ZONE 'Asia/Dhaka'
              AND time < ($3::date + 1)::timestamp AT TIME ZONE 'Asia/Dhaka'
            ORDER BY time LIMIT 2000
            """))
        {
            cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(from); cmd.Parameters.AddWithValue(to);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                events.Add(new { time = r.GetFieldValue<DateTimeOffset>(0), user = r.GetString(1), @event = r.GetString(2) });
        }
        var settings = await EndpointSettings.LoadActivityAsync(ds, ct);
        return Results.Ok(new { from, to, trackingEnabled = settings.Enabled, usage, events });
    }

    // ───────── enrollment keys ─────────

    private static async Task<IResult> ListKeys(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT k.enroll_key_id, k.branch_id, b.branch_code, k.label, k.key_hint, k.expires_at, k.max_uses, k.uses,
                   k.is_revoked, k.created_by, k.created_at
            FROM ep.enroll_key k JOIN org.branch b ON b.branch_id = k.branch_id ORDER BY k.created_at DESC
            """);
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                enrollKeyId = r.GetInt32(0), branchId = r.GetInt32(1), branchCode = r.GetString(2), label = r.IsDBNull(3) ? null : r.GetString(3),
                keyHint = r.GetString(4), expiresAt = r.GetFieldValue<DateTimeOffset>(5), maxUses = r.IsDBNull(6) ? (int?)null : r.GetInt32(6),
                uses = r.GetInt32(7), isRevoked = r.GetBoolean(8), createdBy = r.IsDBNull(9) ? null : r.GetString(9),
                createdAt = r.GetFieldValue<DateTimeOffset>(10),
            });
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateKey(EnrollKeyRequest req, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        var days = Math.Clamp(req.ValidDays ?? 30, 1, 365);
        if (req.MaxUses is < 1) return Results.ValidationProblem(new Dictionary<string, string[]> { ["maxUses"] = ["Must be at least 1"] });
        var key = AgentAuthHandler.NewSecret();
        await using var cmd = ds.CreateCommand("""
            INSERT INTO ep.enroll_key (branch_id, label, key_hash, key_hint, expires_at, max_uses, created_by)
            SELECT $1, $2, $3, $4, now() + make_interval(days => $5), $6, $7 WHERE EXISTS (SELECT 1 FROM org.branch WHERE branch_id = $1)
            RETURNING enroll_key_id, expires_at
            """);
        cmd.Parameters.AddWithValue(req.BranchId);
        cmd.Parameters.AddWithValue((object?)EndpointAgentApi.Cut(req.Label?.Trim(), 100) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(AgentAuthHandler.HashKey(key));
        cmd.Parameters.AddWithValue(key[^6..]);
        cmd.Parameters.AddWithValue(days);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)req.MaxUses ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.AddWithValue(http.User.Identity?.Name ?? "?");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["branchId"] = ["Unknown branch"] });
        var id = r.GetInt32(0); var expires = r.GetFieldValue<DateTimeOffset>(1);
        await r.DisposeAsync();
        db.Record(http, "ENDPOINT_KEY_CREATE", "endpoint_enroll_key", id, newValue: new { req.BranchId, req.Label, days, req.MaxUses });
        await db.SaveChangesAsync(ct);
        // The key itself is shown once; only its hash is stored.
        return Results.Ok(new { enrollKeyId = id, key, expiresAt = expires });
    }

    private static async Task<IResult> RevokeKey(int id, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE ep.enroll_key SET is_revoked = true WHERE enroll_key_id = $1");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        db.Record(http, "ENDPOINT_KEY_REVOKE", "endpoint_enroll_key", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Revoke(int id, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE ep.endpoint SET is_revoked = true WHERE endpoint_id = $1 AND NOT is_revoked");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        db.Record(http, "ENDPOINT_REVOKE", "endpoint", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ───────── software rules ─────────

    private static async Task<IResult> ListRules(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT r.rule_id, r.kind, r.pattern, r.severity, r.note, r.is_enabled,
                   CASE WHEN r.kind = 'BLOCKED'
                        THEN (SELECT count(DISTINCT s.endpoint_id) FROM ep.software s JOIN ep.endpoint e USING (endpoint_id)
                              WHERE NOT e.is_revoked AND s.name ILIKE r.like_pattern)
                        ELSE (SELECT count(*) FROM ep.endpoint e WHERE NOT e.is_revoked AND e.inventory_at IS NOT NULL
                              AND NOT EXISTS (SELECT 1 FROM ep.software s WHERE s.endpoint_id = e.endpoint_id AND s.name ILIKE r.like_pattern))
                   END
            FROM ep.software_rule r ORDER BY r.kind, r.pattern
            """);
        var items = new List<object>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new { ruleId = r.GetInt32(0), kind = r.GetString(1), pattern = r.GetString(2), severity = r.GetString(3),
                            note = r.IsDBNull(4) ? null : r.GetString(4), isEnabled = r.GetBoolean(5), violations = r.GetInt64(6) });
        return Results.Ok(items);
    }

    private static IResult? ValidateRule(SoftwareRuleInput i)
    {
        var errors = new Dictionary<string, string[]>();
        if (i.Kind is not ("BLOCKED" or "REQUIRED")) errors["kind"] = ["BLOCKED or REQUIRED"];
        if (string.IsNullOrWhiteSpace(i.Pattern) || i.Pattern.Length > 200) errors["pattern"] = ["Required, max 200 characters"];
        else if (i.Pattern.Trim().Trim('*', '?').Length < 2) errors["pattern"] = ["Too broad — give at least 2 letters"];
        if (i.Severity is not ("CRITICAL" or "HIGH" or "WARNING" or "INFO")) errors["severity"] = ["CRITICAL, HIGH, WARNING or INFO"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static async Task<IResult> CreateRule(SoftwareRuleInput i, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (ValidateRule(i) is { } bad) return bad;
        await using var cmd = ds.CreateCommand("""
            INSERT INTO ep.software_rule (kind, pattern, like_pattern, severity, note, is_enabled)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING rule_id
            """);
        AddRuleParams(cmd, i);
        var id = (int)(await cmd.ExecuteScalarAsync(ct))!;
        db.Record(http, "SOFTWARE_RULE_CREATE", "software_rule", id, newValue: i);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ruleId = id });
    }

    private static async Task<IResult> UpdateRule(int id, SoftwareRuleInput i, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (ValidateRule(i) is { } bad) return bad;
        await using var cmd = ds.CreateCommand("""
            UPDATE ep.software_rule SET kind = $1, pattern = $2, like_pattern = $3, severity = $4, note = $5, is_enabled = $6
            WHERE rule_id = $7
            """);
        AddRuleParams(cmd, i);
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        db.Record(http, "SOFTWARE_RULE_UPDATE", "software_rule", id, newValue: i);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteRule(int id, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("DELETE FROM ep.software_rule WHERE rule_id = $1");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        db.Record(http, "SOFTWARE_RULE_DELETE", "software_rule", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void AddRuleParams(NpgsqlCommand cmd, SoftwareRuleInput i)
    {
        cmd.Parameters.AddWithValue(i.Kind);
        cmd.Parameters.AddWithValue(i.Pattern.Trim());
        cmd.Parameters.AddWithValue(Wildcard.ToLike(i.Pattern));
        cmd.Parameters.AddWithValue(i.Severity);
        cmd.Parameters.AddWithValue((object?)EndpointAgentApi.Cut(i.Note, 300) ?? DBNull.Value);
        cmd.Parameters.AddWithValue(i.IsEnabled);
    }

    // ───────── settings ─────────

    private static async Task<IResult> SaveSettings(ActivitySettingsInput i, HttpContext http, NpgsqlDataSource ds, NmsDb db, CancellationToken ct)
    {
        if (i.RetentionDays is < 7 or > 365)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["retentionDays"] = ["7–365 days"] });
        var old = await EndpointSettings.LoadActivityAsync(ds, ct);
        if (i.Enabled && !old.Enabled && !i.NoticeConfirmed)
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["noticeConfirmed"] = ["Confirm that staff have been informed under PDCL's written monitoring policy"] });
        var user = http.User.Identity?.Name ?? "?";
        var value = new ActivitySettings(i.Enabled, i.RetentionDays,
            i.Enabled ? (old.Enabled ? old.EnabledBy : user) : null,
            i.Enabled ? (old.Enabled ? old.EnabledAt : DateTimeOffset.UtcNow) : null);
        await EndpointSettings.SaveAsync(ds, "activity", value, user, ct);
        db.Record(http, "ENDPOINT_ACTIVITY_SETTINGS", "endpoint_setting", "activity", old, value);
        await db.SaveChangesAsync(ct);
        return Results.Ok(value);
    }

    private static async Task<int?> BranchOf(NpgsqlDataSource ds, int endpointId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT branch_id FROM ep.endpoint WHERE endpoint_id = $1 AND NOT is_revoked");
        cmd.Parameters.AddWithValue(endpointId);
        return await cmd.ExecuteScalarAsync(ct) as int?;
    }
}
