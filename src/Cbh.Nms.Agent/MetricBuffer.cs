using Cbh.Nms.Shared;
using Microsoft.Data.Sqlite;

namespace Cbh.Nms.Agent;

/// <summary>
/// Local store-and-forward buffer (SRS §3.3, NFR-05). The SQLite AUTOINCREMENT rowid is the agent's
/// sequence number: it never repeats, even after deletes, so HO can deduplicate re-sent rows.
/// Rows flagged as events (state changes such as UPS on-battery) survive pruning.
/// </summary>
public sealed class MetricBuffer : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Lock _lock = new();

    public MetricBuffer(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS metric (
                seq      INTEGER PRIMARY KEY AUTOINCREMENT,
                time_ms  INTEGER NOT NULL,
                entity   TEXT    NOT NULL,
                id       INTEGER NOT NULL,
                key      TEXT    NOT NULL,
                value    REAL    NOT NULL,
                is_event INTEGER NOT NULL DEFAULT 0)
            """);
    }

    public void Add(IEnumerable<BufferedMetric> items)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO metric (time_ms, entity, id, key, value, is_event) VALUES ($t, $e, $i, $k, $v, $ev)";
            var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            var e = cmd.Parameters.Add("$e", SqliteType.Text);
            var i = cmd.Parameters.Add("$i", SqliteType.Integer);
            var k = cmd.Parameters.Add("$k", SqliteType.Text);
            var v = cmd.Parameters.Add("$v", SqliteType.Real);
            var ev = cmd.Parameters.Add("$ev", SqliteType.Integer);
            foreach (var m in items)
            {
                t.Value = m.Time.ToUnixTimeMilliseconds();
                e.Value = m.Entity; i.Value = m.Id; k.Value = m.Key; v.Value = m.Value; ev.Value = m.IsEvent ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Oldest rows first, so a reconnecting branch back-fills history in order.</summary>
    public List<MetricItem> Peek(int max)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, time_ms, entity, id, key, value FROM metric ORDER BY seq LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            var list = new List<MetricItem>();
            while (r.Read())
                list.Add(new MetricItem(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                    r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetDouble(5)));
            return list;
        }
    }

    public void DeleteUpTo(long seq)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM metric WHERE seq <= $s";
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.ExecuteNonQuery();
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM metric";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Drops non-event rows older than the retention window, then the oldest non-event rows
    /// beyond <paramref name="maxRows"/>. Returns how many rows were dropped.</summary>
    public int Prune(TimeSpan retention, int maxRows, DateTimeOffset now)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM metric WHERE is_event = 0 AND time_ms < $cutoff;
                DELETE FROM metric WHERE is_event = 0 AND seq IN (
                    SELECT seq FROM metric WHERE is_event = 0 ORDER BY seq
                    LIMIT max(0, (SELECT count(*) FROM metric) - $max));
                """;
            cmd.Parameters.AddWithValue("$cutoff", (now - retention).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$max", maxRows);
            return cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}

public sealed record BufferedMetric(DateTimeOffset Time, string Entity, int Id, string Key, double Value, bool IsEvent = false);
