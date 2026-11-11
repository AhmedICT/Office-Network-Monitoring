using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cbh.Nms.Endpoint;

public sealed record OutboxRow(long Seq, string Json);

/// <summary>
/// Store-and-forward for uploads (SQLite). Each row is one upload with its own BatchId, so HO ignores a
/// re-send. Reports are capped (oldest dropped first); only the newest inventory is kept.
/// </summary>
public sealed class Outbox : IDisposable
{
    public const string Reports = "report";
    public const string Inventory = "inventory";
    public const string Activity = "activity";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _conn;
    private readonly Lock _lock = new();
    private readonly int _maxRows;

    public Outbox(string path, int maxRows = 20_000)
    {
        _maxRows = maxRows;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS outbox (
                seq     INTEGER PRIMARY KEY AUTOINCREMENT,
                kind    TEXT NOT NULL,
                json    TEXT NOT NULL,
                created INTEGER NOT NULL)
            """);
    }

    public void Add<T>(string kind, T body)
    {
        var json = JsonSerializer.Serialize(new Cbh.Nms.Shared.EndpointUpload<T>(Guid.NewGuid(), body), Json);
        lock (_lock)
        {
            if (kind == Inventory) Exec("DELETE FROM outbox WHERE kind = 'inventory'");
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO outbox (kind, json, created) VALUES ($k, $j, $c)";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
            // Keep activity (it cannot be re-collected); drop the oldest health samples beyond the cap (~2 weeks).
            Exec($"""
                DELETE FROM outbox WHERE kind = 'report' AND seq IN (
                    SELECT seq FROM outbox WHERE kind = 'report' ORDER BY seq DESC LIMIT -1 OFFSET {_maxRows})
                """);
        }
    }

    public List<OutboxRow> Peek(string kind, int max)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT seq, json FROM outbox WHERE kind = $k ORDER BY seq LIMIT $n";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            var rows = new List<OutboxRow>();
            while (r.Read()) rows.Add(new OutboxRow(r.GetInt64(0), r.GetString(1)));
            return rows;
        }
    }

    public void Delete(IEnumerable<long> seqs)
    {
        var list = string.Join(",", seqs);
        if (list.Length == 0) return;
        lock (_lock) Exec($"DELETE FROM outbox WHERE seq IN ({list})");
    }

    public int Count(string kind)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM outbox WHERE kind = $k";
            cmd.Parameters.AddWithValue("$k", kind);
            return Convert.ToInt32(cmd.ExecuteScalar());
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
