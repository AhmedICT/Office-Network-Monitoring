using Npgsql;

namespace Cbh.Nms.Core.Endpoints;

/// <summary>Hourly: deletes activity older than the configured retention, old de-dup markers and old software events.
/// ep.metric has a TimescaleDB retention policy (30 days) instead.</summary>
public sealed class EndpointHousekeeping(NpgsqlDataSource ds, ILogger<EndpointHousekeeping> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var days = (await EndpointSettings.LoadActivityAsync(ds, ct)).RetentionDays;
                await using var cmd = ds.CreateCommand("""
                    DELETE FROM ep.app_usage WHERE day < current_date - @days;
                    DELETE FROM ep.session_event WHERE time < now() - make_interval(days => @days);
                    DELETE FROM ep.ingest_batch WHERE received_at < now() - interval '7 days';
                    DELETE FROM ep.software_event WHERE time < now() - interval '400 days';
                    """);
                cmd.Parameters.AddWithValue("days", days);
                var n = await cmd.ExecuteNonQueryAsync(ct);
                if (n > 0) log.LogInformation("Endpoint housekeeping removed {Rows} old rows (activity retention {Days} days)", n, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Endpoint housekeeping failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
