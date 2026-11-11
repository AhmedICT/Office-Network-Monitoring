using System.Management;
using System.Reflection;
using Cbh.Nms.Endpoint.Activity;
using Cbh.Nms.Endpoint.Collectors;
using Cbh.Nms.Shared;
using Microsoft.Extensions.Options;

namespace Cbh.Nms.Endpoint;

public sealed class EndpointOptions
{
    /// <summary>Core Platform base URL, e.g. https://nms.pdcl.local/</summary>
    public string HoBaseUrl { get; set; } = "";
    /// <summary>Reusable branch enrollment key from "User PCs → Enrollment keys"; read only until registered.</summary>
    public string? EnrollKey { get; set; }
    public string DataDirectory { get; set; } = @"C:\ProgramData\CBH-NMS\Endpoint";
    public TimeSpan SendInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConfigInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Endpoint Agent main loop (D-14): registers once, then every minute collects health + network, every few
/// hours the inventory, and — only while HO says activity tracking is on — application usage from the
/// session helpers. Everything goes through the outbox, so nothing is lost while HO is unreachable.
/// </summary>
public sealed class EndpointService(
    IOptions<EndpointOptions> options, HoClient ho, CredentialStore creds, Outbox outbox,
    HealthCollector health, NetworkCollector network, InventoryCollector inventory,
    ActivityHub hub, ActivityAggregator activity, ILogger<EndpointService> log) : BackgroundService
{
    public static readonly string Version = typeof(EndpointService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private EndpointConfig _config = new(60, 360, false, null);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await RegisterAsync(ct);
        _ = hub.RunAsync(ct);

        var tasks = new[]
        {
            Loop("config", () => options.Value.ConfigInterval, RefreshConfigAsync, ct),
            Loop("report", () => TimeSpan.FromSeconds(Math.Max(15, _config.ReportIntervalS)), CollectReportAsync, ct),
            Loop("inventory", () => TimeSpan.FromMinutes(Math.Max(15, _config.InventoryIntervalMin)), CollectInventoryAsync, ct, TimeSpan.FromSeconds(20)),
            Loop("activity", () => TimeSpan.FromMinutes(1), CollectActivityAsync, ct, TimeSpan.FromMinutes(1)),
            Loop("send", () => options.Value.SendInterval, SendAsync, ct, TimeSpan.FromSeconds(10)),
        };
        await Task.WhenAll(tasks);
    }

    private async Task Loop(string name, Func<TimeSpan> every, Func<CancellationToken, Task> body, CancellationToken ct, TimeSpan? firstDelay = null)
    {
        if (firstDelay is { } d) await Task.Delay(d, ct).ContinueWith(_ => { }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            try { await body(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "{Loop} failed", name); }
            try { await Task.Delay(every(), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (creds.Load() is { } c) { ho.UseCredential(c); log.LogInformation("Endpoint {Id} (branch {Branch}) starting", c.EndpointId, c.BranchId); return; }
            var key = options.Value.EnrollKey;
            if (string.IsNullOrWhiteSpace(key)) log.LogError("Not registered and Endpoint:EnrollKey is empty — set it and restart the service");
            else
                try
                {
                    var res = await ho.RegisterAsync(new EndpointRegisterRequest(key.Trim(), Environment.MachineName, InventoryCollector.MachineGuid(), Version), ct);
                    creds.Save(new EndpointCredential(res.EndpointId, res.BranchId, res.Key));
                    log.LogInformation("Registered as endpoint {Id} for branch {Branch}", res.EndpointId, res.BranchId);
                    continue;
                }
                catch (HttpRequestException ex) { log.LogWarning("Registration failed: {Message}", ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task RefreshConfigAsync(CancellationToken ct)
    {
        if (await ho.GetConfigAsync(ct) is { } cfg)
        {
            if (cfg.ActivityTracking != _config.ActivityTracking)
                log.LogInformation("Activity tracking is now {State} by HO policy", cfg.ActivityTracking ? "ON" : "OFF");
            _config = cfg;
        }
        hub.Track = _config.ActivityTracking;
        if (!hub.Track) activity.Clear();
    }

    private async Task CollectReportAsync(CancellationToken ct)
    {
        var target = _config.ProbeTarget ?? new Uri(options.Value.HoBaseUrl).Host;
        var report = new EndpointReport(DateTimeOffset.UtcNow, ConsoleUser(), health.Collect(), await network.CollectAsync(target, ct));
        outbox.Add(Outbox.Reports, report);
    }

    private Task CollectInventoryAsync(CancellationToken ct)
    {
        outbox.Add(Outbox.Inventory, inventory.Collect());
        return Task.CompletedTask;
    }

    private Task CollectActivityAsync(CancellationToken ct)
    {
        if (!hub.Track) { activity.Clear(); return Task.CompletedTask; }
        if (activity.Drain() is { } batch) outbox.Add(Outbox.Activity, batch);
        return Task.CompletedTask;
    }

    private async Task SendAsync(CancellationToken ct)
    {
        foreach (var (kind, path, max) in new[] { (Outbox.Reports, "endpoint/v1/reports", 60), (Outbox.Activity, "endpoint/v1/activity", 60) })
            for (var guard = 0; guard < 20; guard++)
            {
                var rows = outbox.Peek(kind, max);
                if (rows.Count == 0) break;
                await ho.PostRawAsync(path, "[" + string.Join(",", rows.Select(r => r.Json)) + "]", ct);
                outbox.Delete(rows.Select(r => r.Seq));
            }
        foreach (var row in outbox.Peek(Outbox.Inventory, 1))
        {
            await ho.PostRawAsync("endpoint/v1/inventory", row.Json, ct);
            outbox.Delete([row.Seq]);
        }
    }

    /// <summary>The user at the console (Win32_ComputerSystem.UserName); null when nobody is logged on.</summary>
    private static string? ConsoleUser()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
            return s.Get().Cast<ManagementBaseObject>().FirstOrDefault()?["UserName"] as string;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
