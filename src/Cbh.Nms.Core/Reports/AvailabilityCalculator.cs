using Cbh.Nms.Shared;

namespace Cbh.Nms.Core.Reports;

public readonly record struct StatusInterval(string Status, DateTimeOffset Start, DateTimeOffset? End);
public readonly record struct Window(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// SLA availability (SRS §1.3): (period − unplanned downtime) ÷ period × 100.
/// Downtime = time in Offline-ISP or Offline-Power, minus any overlap with approved maintenance.
/// Degraded and Unknown are not downtime (the branch is up, or we cannot tell).
/// </summary>
public static class AvailabilityCalculator
{
    public static (double AvailabilityPct, TimeSpan Downtime) Compute(
        DateTimeOffset periodStart, DateTimeOffset periodEnd,
        IEnumerable<StatusInterval> history, IEnumerable<Window> maintenance)
    {
        var total = periodEnd - periodStart;
        if (total <= TimeSpan.Zero) return (100, TimeSpan.Zero);

        var down = history
            .Where(h => h.Status is BranchStatus.OfflineIsp or BranchStatus.OfflinePower)
            .Select(h => Clip(h.Start, h.End ?? periodEnd, periodStart, periodEnd))
            .Where(w => w is not null).Select(w => w!.Value);
        var maint = maintenance.Select(m => Clip(m.Start, m.End, periodStart, periodEnd)).Where(w => w is not null).Select(w => w!.Value);

        var downtime = Length(Subtract(Merge(down), Merge(maint)));
        return (Math.Round(100.0 * (total - downtime).TotalSeconds / total.TotalSeconds, 3), downtime);
    }

    private static Window? Clip(DateTimeOffset s, DateTimeOffset e, DateTimeOffset ps, DateTimeOffset pe)
    {
        var start = s > ps ? s : ps;
        var end = e < pe ? e : pe;
        return end > start ? new Window(start, end) : null;
    }

    public static List<Window> Merge(IEnumerable<Window> windows)
    {
        var result = new List<Window>();
        foreach (var w in windows.OrderBy(w => w.Start))
        {
            if (result.Count > 0 && w.Start <= result[^1].End)
                result[^1] = new Window(result[^1].Start, w.End > result[^1].End ? w.End : result[^1].End);
            else result.Add(w);
        }
        return result;
    }

    private static List<Window> Subtract(List<Window> from, List<Window> remove)
    {
        var result = new List<Window>();
        foreach (var w in from)
        {
            var pieces = new List<Window> { w };
            foreach (var r in remove)
                pieces = pieces.SelectMany(p =>
                    r.End <= p.Start || r.Start >= p.End
                        ? [p]
                        : new[] { new Window(p.Start, r.Start), new Window(r.End, p.End) }.Where(x => x.End > x.Start)).ToList();
            result.AddRange(pieces);
        }
        return result;
    }

    private static TimeSpan Length(IEnumerable<Window> ws) => ws.Aggregate(TimeSpan.Zero, (t, w) => t + (w.End - w.Start));
}
