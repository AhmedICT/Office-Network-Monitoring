using System.Text.Json;
using Cbh.Nms.Shared;
using Npgsql;

namespace Cbh.Nms.Core.Endpoints;

public sealed record EndpointFinding(string DedupKey, int BranchId, int EndpointId, string Severity, string Title);

/// <summary>
/// User PC problems the AlertEngine turns into ordinary alerts (D-14): low disk, antivirus missing / off /
/// out of date, firewall off, blocked software installed, required software missing. Based on the newest data
/// each PC sent, so a PC switched off at night keeps its alerts instead of flapping.
/// Evaluated at most once a minute — inventories change slowly and the software join is the costly part.
/// </summary>
public sealed class EndpointAlertSource(NpgsqlDataSource ds)
{
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private List<EndpointFinding> _cached = [];
    private DateTimeOffset _at = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<EndpointFinding>> GetAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (now - _at < Refresh) return _cached;
        var list = new List<EndpointFinding>();
        var settings = await EndpointSettings.LoadAlertsAsync(ds, ct);

        await using (var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.branch_id, e.host_name, b.branch_code, d->>'name',
                   100 * (d->>'freeGb')::float / (d->>'totalGb')::float
            FROM ep.endpoint e JOIN org.branch b USING (branch_id),
                 jsonb_array_elements(coalesce(e.live->'health'->'disks', '[]')) d
            WHERE NOT e.is_revoked AND (d->>'totalGb')::float > 0
              AND 100 * (d->>'freeGb')::float / (d->>'totalGb')::float < $1
            """))
        {
            cmd.Parameters.AddWithValue(settings.DiskFreePct);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var drive = r.GetString(4).TrimEnd('\\');
                list.Add(new($"ep:{r.GetInt32(0)}:disk:{drive}", r.GetInt32(1), r.GetInt32(0), Severity.Warning,
                    $"Low disk {drive} {r.GetDouble(5):0}% free · {r.GetString(3)} · {r.GetString(2)}"));
            }
        }

        await using (var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.branch_id, e.host_name, b.branch_code, e.security::text
            FROM ep.endpoint e JOIN org.branch b USING (branch_id) WHERE NOT e.is_revoked AND e.security IS NOT NULL
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var sec = JsonSerializer.Deserialize<SecurityInfo>(r.GetString(4), Json);
                if (sec is null) continue;
                foreach (var (kind, severity, text) in SecurityFindings(sec))
                    list.Add(new($"ep:{r.GetInt32(0)}:{kind}", r.GetInt32(1), r.GetInt32(0), severity,
                        $"{text} · {r.GetString(3)} · {r.GetString(2)}"));
            }

        await using (var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.branch_id, e.host_name, b.branch_code, r.rule_id, r.severity, min(s.name)
            FROM ep.software_rule r
            JOIN ep.software s ON s.name ILIKE r.like_pattern
            JOIN ep.endpoint e ON e.endpoint_id = s.endpoint_id AND NOT e.is_revoked
            JOIN org.branch b ON b.branch_id = e.branch_id
            WHERE r.is_enabled AND r.kind = 'BLOCKED'
            GROUP BY e.endpoint_id, e.branch_id, e.host_name, b.branch_code, r.rule_id, r.severity
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new($"ep:{r.GetInt32(0)}:blocked:{r.GetInt32(4)}", r.GetInt32(1), r.GetInt32(0), r.GetString(5),
                    $"Blocked software \"{EndpointAgentApi.Cut(r.GetString(6), 60)}\" · {r.GetString(3)} · {r.GetString(2)}"));

        await using (var cmd = ds.CreateCommand("""
            SELECT e.endpoint_id, e.branch_id, e.host_name, b.branch_code, r.rule_id, r.severity, r.pattern
            FROM ep.software_rule r CROSS JOIN ep.endpoint e JOIN org.branch b ON b.branch_id = e.branch_id
            WHERE r.is_enabled AND r.kind = 'REQUIRED' AND NOT e.is_revoked AND e.inventory_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM ep.software s WHERE s.endpoint_id = e.endpoint_id AND s.name ILIKE r.like_pattern)
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                list.Add(new($"ep:{r.GetInt32(0)}:missing:{r.GetInt32(4)}", r.GetInt32(1), r.GetInt32(0), r.GetString(5),
                    $"Required software missing \"{r.GetString(6)}\" · {r.GetString(3)} · {r.GetString(2)}"));

        _cached = list;
        _at = now;
        return list;
    }

    /// <summary>(dedup suffix, severity, text) for one PC's security state.</summary>
    public static IEnumerable<(string Kind, string Severity, string Text)> SecurityFindings(SecurityInfo s)
    {
        if (s.AntivirusSupported)
        {
            if (s.Antivirus.Count == 0) yield return ("av", Severity.High, "No antivirus");
            else if (!s.Antivirus.Any(a => a.Enabled)) yield return ("av", Severity.High, "Antivirus disabled");
            else if (!s.Antivirus.Any(a => a.Enabled && a.UpToDate)) yield return ("av-old", Severity.Warning, "Antivirus out of date");
        }
        var off = new List<string>();
        if (s.FirewallDomain == false) off.Add("domain");
        if (s.FirewallPrivate == false) off.Add("private");
        if (s.FirewallPublic == false) off.Add("public");
        if (off.Count > 0) yield return ("fw", Severity.Warning, $"Windows Firewall off ({string.Join(", ", off)})");
    }
}
