using System.Net.NetworkInformation;

namespace Cbh.Nms.Agent.Collectors;

public sealed record PingResult(bool Up, double? LatencyMs, double? JitterMs, double LossPct);

/// <summary>ICMP probe: a short burst gives latency, jitter (mean successive difference) and loss (FR-AV-01).</summary>
public static class PingProbe
{
    public static async Task<PingResult> RunAsync(string target, int count = 4, int timeoutMs = 2000, CancellationToken ct = default)
    {
        using var ping = new Ping();
        var rtts = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: ct);
                if (reply.Status == IPStatus.Success) rtts.Add(reply.RoundtripTime);
            }
            catch (PingException) { /* counts as lost */ }
            if (i < count - 1) await Task.Delay(200, ct);
        }
        return Summarize(rtts, count);
    }

    public static PingResult Summarize(IReadOnlyList<double> rtts, int sent)
    {
        var loss = 100.0 * (sent - rtts.Count) / sent;
        if (rtts.Count == 0) return new PingResult(false, null, null, loss);
        double? jitter = rtts.Count > 1
            ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).Average()
            : null;
        return new PingResult(true, rtts.Average(), jitter, loss);
    }
}
