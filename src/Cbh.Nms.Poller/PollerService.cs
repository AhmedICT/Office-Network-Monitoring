using System.Net;
using System.Net.NetworkInformation;
using Npgsql;
using NpgsqlTypes;

namespace Cbh.Nms.Poller;

/// <summary>
/// Central Poller (SRS §3.1): from HO, pings every active branch's MPLS router every 30 s, independent of
/// the Branch Agents. Results go to mon.reachability; the Core's StatusEngine combines them with agent
/// heartbeats to tell Offline (router unreachable) from Unknown (agent silent, router fine).
/// </summary>
public sealed class PollerService(NpgsqlDataSource db, ILogger<PollerService> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int Attempts = 2;      // a single dropped echo should not count as a failure
    private const int TimeoutMs = 2000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Poll cycle failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var targets = new List<(int BranchId, IPAddress Ip)>();
        await using (var cmd = db.CreateCommand(
            "SELECT branch_id, mpls_router_ip FROM org.branch WHERE is_active AND mpls_router_ip IS NOT NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) targets.Add((r.GetInt32(0), r.GetFieldValue<IPAddress>(1)));

        var now = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(targets.Select(async t => (t.BranchId, Rtt: await PingAsync(t.Ip, ct))));

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var copy = await conn.BeginBinaryImportAsync(
            "COPY mon.reachability (time, branch_id, success, rtt_ms) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var (branchId, rtt) in results)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
            await copy.WriteAsync(branchId, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(rtt is not null, NpgsqlDbType.Boolean, ct);
            if (rtt is { } v) await copy.WriteAsync(v, NpgsqlDbType.Real, ct);
            else await copy.WriteNullAsync(ct);
        }
        await copy.CompleteAsync(ct);

        var down = results.Count(r => r.Rtt is null);
        if (down > 0) log.LogInformation("Poll: {Down}/{Total} branch routers unreachable", down, results.Length);
    }

    private static async Task<float?> PingAsync(IPAddress ip, CancellationToken ct)
    {
        using var ping = new Ping();
        for (var i = 0; i < Attempts; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(TimeoutMs), cancellationToken: ct);
                if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
            }
            catch (PingException) { }
        }
        return null;
    }
}
