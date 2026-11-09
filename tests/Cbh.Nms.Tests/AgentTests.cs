using Cbh.Nms.Agent;
using Cbh.Nms.Agent.Collectors;
using Cbh.Nms.Shared;
using Cbh.Nms.Shared;

namespace Cbh.Nms.Tests;

public sealed class MetricBufferTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cbhnms-test-{Guid.NewGuid():N}.db");
    private readonly MetricBuffer _buffer;
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);

    public MetricBufferTests() => _buffer = new MetricBuffer(_path);

    private static BufferedMetric M(DateTimeOffset t, double v = 1, bool ev = false) =>
        new(t, EntityTypes.Device, 140, MetricKeys.CpuPct, v, ev);

    [Fact]
    public void Peek_returns_oldest_first_with_increasing_seq()
    {
        _buffer.Add([M(Now, 1), M(Now, 2), M(Now, 3)]);
        var rows = _buffer.Peek(10);
        Assert.Equal([1d, 2d, 3d], rows.Select(r => r.Value));
        Assert.True(rows[0].Seq < rows[1].Seq && rows[1].Seq < rows[2].Seq);
    }

    [Fact]
    public void Rows_are_deleted_only_up_to_the_acked_seq()
    {
        _buffer.Add([M(Now, 1), M(Now, 2), M(Now, 3)]);
        var rows = _buffer.Peek(10);
        _buffer.DeleteUpTo(rows[1].Seq);
        Assert.Equal(3d, Assert.Single(_buffer.Peek(10)).Value);
    }

    [Fact]
    public void Seq_is_never_reused_after_delete()
    {
        _buffer.Add([M(Now)]);
        var first = _buffer.Peek(1)[0].Seq;
        _buffer.DeleteUpTo(first);
        _buffer.Add([M(Now)]);
        Assert.True(_buffer.Peek(1)[0].Seq > first);
    }

    [Fact]
    public void Seq_survives_reopen()
    {
        _buffer.Add([M(Now)]);
        var first = _buffer.Peek(1)[0].Seq;
        _buffer.DeleteUpTo(first);
        _buffer.Dispose();
        using var reopened = new MetricBuffer(_path);
        reopened.Add([M(Now)]);
        Assert.True(reopened.Peek(1)[0].Seq > first);
    }

    [Fact]
    public void Prune_drops_expired_rows_but_keeps_events()
    {
        _buffer.Add([M(Now.AddHours(-80), 1), M(Now.AddHours(-80), 2, ev: true), M(Now.AddHours(-1), 3)]);
        _buffer.Prune(TimeSpan.FromHours(72), maxRows: 1000, Now);
        Assert.Equal([2d, 3d], _buffer.Peek(10).Select(r => r.Value));
    }

    [Fact]
    public void Prune_over_capacity_drops_oldest_non_events()
    {
        _buffer.Add([M(Now, 1, ev: true), M(Now, 2), M(Now, 3), M(Now, 4)]);
        _buffer.Prune(TimeSpan.FromHours(72), maxRows: 2, Now);
        Assert.Equal([1d, 4d], _buffer.Peek(10).Select(r => r.Value));
    }

    public void Dispose()
    {
        _buffer.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public class StateTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void First_sample_is_not_an_event_and_a_change_is()
    {
        var t = new StateTracker();
        Assert.False(t.Mark(new(Now, "DEVICE", 143, MetricKeys.UpsOnBattery, 0)).IsEvent);
        Assert.False(t.Mark(new(Now, "DEVICE", 143, MetricKeys.UpsOnBattery, 0)).IsEvent);
        Assert.True(t.Mark(new(Now, "DEVICE", 143, MetricKeys.UpsOnBattery, 1)).IsEvent);
    }

    [Fact]
    public void Non_state_metrics_are_never_events()
    {
        var t = new StateTracker();
        t.Mark(new(Now, "DEVICE", 1, MetricKeys.CpuPct, 10));
        Assert.False(t.Mark(new(Now, "DEVICE", 1, MetricKeys.CpuPct, 90)).IsEvent);
    }
}

public class ProbeTests
{
    [Fact]
    public void Ping_summary_computes_loss_latency_and_jitter()
    {
        var r = PingProbe.Summarize([10, 20, 15], sent: 4);
        Assert.True(r.Up);
        Assert.Equal(25, r.LossPct);
        Assert.Equal(15, r.LatencyMs);
        Assert.Equal(7.5, r.JitterMs);   // (|20-10| + |15-20|) / 2
    }

    [Fact]
    public void All_lost_is_down_with_full_loss()
    {
        var r = PingProbe.Summarize([], sent: 4);
        Assert.False(r.Up);
        Assert.Equal(100, r.LossPct);
        Assert.Null(r.LatencyMs);
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.33.1.4.1.0|eq:5", 5, 1)]
    [InlineData("1.3.6.1.2.1.33.1.4.1.0|eq:5", 3, 0)]
    [InlineData("1.3.6.1.2.1.1.3.0|div:100", 12345, 123.45)]
    [InlineData("1.3.6.1.4.1.12356.101.4.1.3.0", 37, 37)]
    public void Oid_transforms(string spec, double raw, double expected) =>
        Assert.Equal(expected, OidSpec.Parse(spec).Apply(raw), precision: 6);

    [Fact]
    public void Bad_oid_transform_is_rejected() =>
        Assert.Throws<FormatException>(() => OidSpec.Parse("1.3.6.1|eq"));

    [Theory]
    [InlineData("10.1.0.10:1433", "10.1.0.10", 1433)]
    [InlineData("his-db.pdcl.local:5432", "his-db.pdcl.local", 5432)]
    public void Tcp_target_parsing(string target, string host, int port) =>
        Assert.Equal((host, port), AppProbe.SplitHostPort(target));
}
