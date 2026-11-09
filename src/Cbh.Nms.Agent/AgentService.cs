using System.Diagnostics;
using System.Reflection;
using Cbh.Nms.Agent.Collectors;
using Cbh.Nms.Shared;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Agent;

/// <summary>
/// The Branch Agent: polls local targets, buffers results in SQLite, and ships them to HO.
/// It only collects; deciding what is a problem happens at HO (SRS §2.1).
/// </summary>
public sealed class AgentService(
    IOptions<AgentOptions> options, HoClient ho, AppProbe appProbe, MetricBuffer buffer,
    CredentialStore credentials, ConfigCache configCache, ILogger<AgentService> log) : BackgroundService
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly AgentOptions _opt = options.Value;
    private readonly StateTracker _states = new();
    private readonly SemaphoreSlim _flushNow = new(0, 1);
    private readonly Dictionary<int, DateTimeOffset> _deviceDue = [];
    private volatile AgentConfig? _config;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("CBH-NMS Branch Agent {Version} starting", Version);
        var cred = await EnsureRegisteredAsync(ct);
        ho.UseCredential(cred);

        _config = configCache.Load();
        await RefreshConfigAsync(ct);

        await Task.WhenAll(
            Loop("poll", () => TimeSpan.FromSeconds(_config?.PollIntervalSeconds ?? 60), PollOnceAsync, ct),
            SendLoopAsync(ct),
            Loop("heartbeat", () => _opt.HeartbeatInterval, HeartbeatAsync, ct),
            Loop("config", () => _opt.ConfigInterval, RefreshConfigAsync, ct));
    }

    private async Task<AgentCredential> EnsureRegisteredAsync(CancellationToken ct)
    {
        if (credentials.Load() is { } existing) return existing;
        if (string.IsNullOrWhiteSpace(_opt.EnrollToken))
            throw new InvalidOperationException("Agent is not registered and Agent:EnrollToken is not set");

        while (true)
        {
            try
            {
                var res = await ho.RegisterAsync(new RegisterRequest(_opt.EnrollToken, Environment.MachineName, Version), ct);
                var cred = new AgentCredential(res.AgentId, res.BranchId, res.Key);
                credentials.Save(cred);
                log.LogInformation("Registered as agent {AgentId} for branch {BranchId}", res.AgentId, res.BranchId);
                return cred;
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Registration failed; retrying in 60 s");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
    }

    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        try
        {
            var fresh = await ho.GetConfigAsync(_config?.ConfigVersion, ct);
            if (fresh is null) return;
            _config = fresh;
            configCache.Save(fresh);
            log.LogInformation("Config v{Version}: {Devices} devices, {Links} ISP links, {Apps} app checks",
                fresh.ConfigVersion, fresh.Devices.Count, fresh.IspLinks.Count, fresh.AppChecks.Count);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Config pull failed ({Message}); keeping v{Version}", ex.Message, _config?.ConfigVersion);
        }
    }

    // ───────── polling ─────────

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (_config is not { } cfg) return;
        var now = DateTimeOffset.UtcNow;
        var results = new List<BufferedMetric>();
        var gate = new SemaphoreSlim(16);

        async Task Run(Func<Task<IEnumerable<BufferedMetric>>> probe)
        {
            await gate.WaitAsync(ct);
            try { var r = await probe(); lock (results) results.AddRange(r); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Probe failed"); }
            finally { gate.Release(); }
        }

        var tasks = new List<Task>();
        if (cfg.HoProbeTarget is { } ho)
            tasks.Add(Run(async () => PingMetrics(EntityTypes.Branch, cfg.BranchId, await PingProbe.RunAsync(ho, ct: ct), now)));

        foreach (var link in cfg.IspLinks)
            tasks.Add(Run(async () => PingMetrics(EntityTypes.IspLink, link.IspLinkId, await PingProbe.RunAsync(link.ProbeTarget, ct: ct), now)));

        foreach (var d in cfg.Devices)
        {
            if (_deviceDue.TryGetValue(d.DeviceId, out var due) && due > now) continue;
            _deviceDue[d.DeviceId] = now.AddSeconds(Math.Max(15, d.PollIntervalSeconds));
            tasks.Add(Run(() => PollDeviceAsync(d, now, ct)));
        }

        foreach (var app in cfg.AppChecks)
            tasks.Add(Run(async () =>
            {
                var r = await appProbe.RunAsync(app, ct);
                var list = new List<BufferedMetric>
                {
                    new(now, EntityTypes.App, app.AppCheckId, MetricKeys.Up, r.Up ? 1 : 0),
                    new(now, EntityTypes.App, app.AppCheckId, MetricKeys.ResponseMs, r.ResponseMs),
                };
                if (r.HttpStatus is { } code) list.Add(new(now, EntityTypes.App, app.AppCheckId, MetricKeys.HttpStatus, code));
                return list;
            }));

        await Task.WhenAll(tasks);

        var marked = results.Select(m => _states.Mark(m)).ToList();
        buffer.Add(marked);
        if (marked.Any(m => m.IsEvent)) _flushNow.TryRelease();   // state change: don't wait for the 60 s batch
    }

    private async Task<IEnumerable<BufferedMetric>> PollDeviceAsync(DeviceTarget d, DateTimeOffset now, CancellationToken ct)
    {
        var list = PingMetrics(EntityTypes.Device, d.DeviceId, await PingProbe.RunAsync(d.IpAddress, count: 2, ct: ct), now).ToList();
        if (d.Snmp is { } cred && d.Oids.Count > 0 && list.Any(m => m.Key == MetricKeys.Up && m.Value == 1))
        {
            try
            {
                foreach (var (key, value) in await SnmpProbe.GetAsync(d.IpAddress, cred, d.Oids, _opt.SnmpTimeoutMs, ct))
                    list.Add(new(now, EntityTypes.Device, d.DeviceId, key, value));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("SNMP poll of {Ip} failed: {Message}", d.IpAddress, ex.Message);
            }
        }
        return list;
    }

    private static IEnumerable<BufferedMetric> PingMetrics(string entity, int id, PingResult r, DateTimeOffset now)
    {
        yield return new(now, entity, id, MetricKeys.Up, r.Up ? 1 : 0);
        yield return new(now, entity, id, MetricKeys.LossPct, r.LossPct);
        if (r.LatencyMs is { } lat) yield return new(now, entity, id, MetricKeys.LatencyMs, lat);
        if (r.JitterMs is { } jit) yield return new(now, entity, id, MetricKeys.JitterMs, jit);
    }

    // ───────── sending ─────────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _flushNow.WaitAsync(_opt.SendInterval, ct);
            try
            {
                var dropped = buffer.Prune(_opt.BufferRetention, _opt.BufferMaxRows, DateTimeOffset.UtcNow);
                if (dropped > 0) log.LogWarning("Buffer full/expired: dropped {Rows} oldest non-event rows", dropped);
                await DrainAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Send failed ({Message}); {Rows} rows buffered", ex.Message, buffer.Count());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Send loop error"); }
        }
    }

    /// <summary>Sends oldest-first batches until the buffer is empty; rows are deleted only once HO acks them.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            var batch = buffer.Peek(AgentLimits.MaxBatchRows);
            if (batch.Count == 0) return;
            var result = await ho.PostMetricsAsync(
                new MetricBatch(ho.AgentId, $"{ho.AgentId}-{batch[0].Seq}", batch), ct);
            if (result.RetryAfter is { } wait)
            {
                log.LogInformation("HO busy; retrying in {Seconds} s", wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }
            if (result.AckedUpToSeq is { } acked) buffer.DeleteUpTo(acked);
            if (batch.Count < AgentLimits.MaxBatchRows) return;
        }
    }

    // ───────── heartbeat ─────────

    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastCpuAt = DateTime.UtcNow;

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpu = p.TotalProcessorTime;
        var cpuPct = 100 * (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount;
        (_lastCpu, _lastCpuAt) = (cpu, now);
        try
        {
            await ho.PostHeartbeatAsync(new Heartbeat(Version, _config?.ConfigVersion ?? 0, buffer.Count(),
                Math.Round(cpuPct, 1), Math.Round(p.WorkingSet64 / 1048576.0, 1)), ct);
        }
        catch (HttpRequestException ex) { log.LogDebug("Heartbeat failed: {Message}", ex.Message); }
    }

    private async Task Loop(string name, Func<TimeSpan> interval, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try { await body(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "{Loop} loop error", name); }
            var wait = interval() - Stopwatch.GetElapsedTime(started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        }
    }
}

/// <summary>Flags a metric as an event when a state key changes value, so the buffer never prunes it (§3.3).</summary>
public sealed class StateTracker
{
    private static readonly HashSet<string> StateKeys = [MetricKeys.Up, MetricKeys.UpsOnBattery];
    private readonly Dictionary<(string, int, string), double> _last = [];

    public BufferedMetric Mark(BufferedMetric m)
    {
        if (!StateKeys.Contains(m.Key)) return m;
        var k = (m.Entity, m.Id, m.Key);
        var changed = _last.TryGetValue(k, out var prev) && prev != m.Value;
        _last[k] = m.Value;
        return changed ? m with { IsEvent = true } : m;
    }
}

internal static class SemaphoreExtensions
{
    public static void TryRelease(this SemaphoreSlim s)
    {
        try { if (s.CurrentCount == 0) s.Release(); } catch (SemaphoreFullException) { }
    }
}
