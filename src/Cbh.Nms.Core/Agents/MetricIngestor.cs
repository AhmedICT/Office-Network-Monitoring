using Cbh.Nms.Shared;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;

namespace Cbh.Nms.Core.Agents;

/// <summary>
/// Writes agent metric batches into mon.metric_raw: binary COPY into a temp table, then
/// INSERT … ON CONFLICT DO NOTHING so a re-sent batch is stored once (NFR-05, SRS §6.4.4).
/// </summary>
public sealed class MetricIngestor(NpgsqlDataSource dataSource, IMemoryCache cache, ILogger<MetricIngestor> log)
{
    public async Task<int> IngestAsync(int agentId, int branchId, IReadOnlyList<MetricItem> items, CancellationToken ct)
    {
        var owned = await GetOwnedEntitiesAsync(branchId, ct);
        var accepted = items.Where(i => owned.Contains((i.Entity, i.Id)) && double.IsFinite(i.Value)).ToList();
        if (accepted.Count < items.Count)
            // Usually a device removed since the agent's last config pull; ack anyway so it is not resent forever.
            log.LogWarning("Agent {AgentId}: dropped {Count} metric rows for entities outside branch {BranchId}",
                agentId, items.Count - accepted.Count, branchId);
        if (accepted.Count == 0) return 0;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE stage_metric (LIKE mon.metric_raw) ON COMMIT DROP", conn, tx))
            await create.ExecuteNonQueryAsync(ct);

        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY stage_metric (time, entity_type, entity_id, metric_key, value, agent_id, agent_seq) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var i in accepted)
            {
                await copy.StartRowAsync(ct);
                await copy.WriteAsync(i.Time.ToUniversalTime(), NpgsqlDbType.TimestampTz, ct);
                await copy.WriteAsync(i.Entity, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(i.Id, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(i.Key, NpgsqlDbType.Varchar, ct);
                await copy.WriteAsync(i.Value, NpgsqlDbType.Double, ct);
                await copy.WriteAsync(agentId, NpgsqlDbType.Integer, ct);
                await copy.WriteAsync(i.Seq, NpgsqlDbType.Bigint, ct);
            }
            await copy.CompleteAsync(ct);
        }

        int inserted;
        await using (var merge = new NpgsqlCommand(
            "INSERT INTO mon.metric_raw SELECT * FROM stage_metric ON CONFLICT DO NOTHING", conn, tx))
            inserted = await merge.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return inserted;
    }

    /// <summary>The (entity_type, id) pairs an agent of this branch may report on (SRS §7.1 authorization).</summary>
    private async Task<HashSet<(string, int)>> GetOwnedEntitiesAsync(int branchId, CancellationToken ct)
    {
        return (await cache.GetOrCreateAsync($"owned:{branchId}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var set = new HashSet<(string, int)> { (EntityTypes.Branch, branchId) };
            await using var cmd = dataSource.CreateCommand("""
                SELECT 'DEVICE', device_id FROM inv.device WHERE branch_id = $1
                UNION ALL SELECT 'ISPLINK', isp_link_id FROM org.isp_link WHERE branch_id = $1
                UNION ALL SELECT 'APP', app_check_id FROM inv.app_check WHERE branch_id = $1 OR branch_id IS NULL
                """);
            cmd.Parameters.AddWithValue(branchId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) set.Add((r.GetString(0), r.GetInt32(1)));
            return set;
        }))!;
    }
}
