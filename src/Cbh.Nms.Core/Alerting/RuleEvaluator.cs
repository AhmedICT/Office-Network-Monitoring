namespace Cbh.Nms.Core.Alerting;

public enum RuleState
{
    /// <summary>No recent data — leave any existing alert as it is (branch status covers silence).</summary>
    Stale,
    /// <summary>Latest sample is normal: clear any active alert.</summary>
    Normal,
    /// <summary>Condition true, but not yet for the rule's full duration.</summary>
    Pending,
    Firing,
}

public readonly record struct Sample(DateTimeOffset Time, double Value);

/// <summary>Pure threshold-rule evaluation (FR-AL-01): "metric OP threshold, continuously for N seconds".</summary>
public static class RuleEvaluator
{
    /// <summary>Samples older than this make the entity stale. Covers a 60 s poll + 60 s send + slack.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(180);

    public static bool Matches(string op, double value, double threshold) => op switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        "=" => Math.Abs(value - threshold) < 1e-9,
        _ => throw new ArgumentException($"Unknown operator '{op}'"),
    };

    /// <param name="samples">Samples for one entity, any order.</param>
    public static (RuleState State, double? Value) Evaluate(IReadOnlyList<Sample> samples, string op, double threshold,
        int forSeconds, DateTimeOffset now)
    {
        if (samples.Count == 0) return (RuleState.Stale, null);
        var ordered = samples.OrderByDescending(s => s.Time).ToList();
        var latest = ordered[0];
        if (now - latest.Time > StaleAfter) return (RuleState.Stale, latest.Value);
        if (!Matches(op, latest.Value, threshold)) return (RuleState.Normal, latest.Value);

        // Walk back while the condition holds; the oldest such sample is when the problem started.
        var since = latest.Time;
        foreach (var s in ordered.Skip(1))
        {
            if (!Matches(op, s.Value, threshold)) break;
            since = s.Time;
        }
        return (latest.Time - since >= TimeSpan.FromSeconds(forSeconds) ? RuleState.Firing : RuleState.Pending, latest.Value);
    }
}
