using System.Text.Json;
using Npgsql;

namespace Cbh.Nms.Core.Endpoints;

/// <param name="Enabled">Application usage and session events are collected only while this is on.</param>
/// <param name="RetentionDays">Activity rows older than this are deleted (default 90).</param>
public sealed record ActivitySettings(bool Enabled = false, int RetentionDays = 90, string? EnabledBy = null, DateTimeOffset? EnabledAt = null);

public sealed record EndpointAlertSettings(double DiskFreePct = 10);

/// <summary>ep.setting rows. Read on every config pull / ingest, so a policy change takes effect within minutes.</summary>
public static class EndpointSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<ActivitySettings> LoadActivityAsync(NpgsqlDataSource ds, CancellationToken ct) =>
        await LoadAsync<ActivitySettings>(ds, "activity", ct) ?? new();

    public static async Task<EndpointAlertSettings> LoadAlertsAsync(NpgsqlDataSource ds, CancellationToken ct) =>
        await LoadAsync<EndpointAlertSettings>(ds, "alerts", ct) ?? new();

    public static async Task<T?> LoadAsync<T>(NpgsqlDataSource ds, string key, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM ep.setting WHERE key = $1");
        cmd.Parameters.AddWithValue(key);
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<T>(s, Json) : default;
    }

    public static async Task SaveAsync<T>(NpgsqlDataSource ds, string key, T value, string user, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO ep.setting (key, value, updated_by, updated_at) VALUES ($1, $2::jsonb, $3, now())
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_by = excluded.updated_by, updated_at = now()
            """);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(value, Json));
        cmd.Parameters.AddWithValue(user);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
