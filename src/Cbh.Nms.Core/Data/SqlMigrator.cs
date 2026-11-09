using Npgsql;

namespace Cbh.Nms.Core.Data;

/// <summary>
/// Applies db/migrations/NNN_*.sql in order, each in its own transaction, recording what ran in
/// public.schema_migrations. The SQL files (not EF migrations) own the schema because TimescaleDB
/// hypertables, policies and continuous aggregates are not expressible in EF.
/// </summary>
public sealed class SqlMigrator(NpgsqlDataSource dataSource, ILogger<SqlMigrator> log)
{
    public async Task MigrateAsync(string directory, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Exec(conn, null, """
            CREATE TABLE IF NOT EXISTS public.schema_migrations (
                version    varchar(100) PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now())
            """, ct);

        // Serialize migrators if two app-pool workers start at once.
        await Exec(conn, null, "SELECT pg_advisory_lock(727001)", ct);
        try
        {
            var applied = new HashSet<string>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM public.schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetString(0));

            foreach (var file in Directory.GetFiles(directory, "*.sql").Order(StringComparer.Ordinal))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                if (applied.Contains(version)) continue;

                log.LogInformation("Applying migration {Version}", version);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, tx, await File.ReadAllTextAsync(file, ct), ct);
                await using (var ins = new NpgsqlCommand("INSERT INTO public.schema_migrations(version) VALUES (@v)", conn, tx))
                {
                    ins.Parameters.AddWithValue("v", version);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await Exec(conn, null, "SELECT pg_advisory_unlock(727001)", CancellationToken.None);
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
