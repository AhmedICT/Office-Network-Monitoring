using Cbh.Nms.Shared;

namespace Cbh.Nms.Endpoint.Activity;

/// <summary>
/// Collects what the session helpers report — foreground seconds per app per user per day, and session
/// state changes (logon, lock, idle…) — until the next upload. Thread-safe: one helper per logged-on session.
/// </summary>
public sealed class ActivityAggregator
{
    /// <summary>A message covers one minute; anything above this is a bug or tampering.</summary>
    public const int MaxSecondsPerMessage = 120;

    private readonly Lock _lock = new();
    private readonly Dictionary<(DateOnly Day, string User, string App), (int Seconds, string? Product)> _usage = [];
    private readonly List<SessionEventItem> _events = [];
    private readonly Dictionary<string, (int Connections, string State)> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public void Connected(string user, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(user, out var s)) _sessions[user] = (s.Connections + 1, s.State);
            else
            {
                _sessions[user] = (1, SessionStates.Active);
                _events.Add(new(now, user, SessionEvents.Logon));
            }
        }
    }

    public void Disconnected(string user, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(user, out var s)) return;
            if (s.Connections > 1) { _sessions[user] = (s.Connections - 1, s.State); return; }
            _sessions.Remove(user);
            _events.Add(new(now, user, SessionEvents.Logoff));
        }
    }

    public void Report(string user, SessionHelperMessage m, DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        lock (_lock)
        {
            foreach (var (app, use) in m.Apps.Take(SessionPipe.MaxAppsPerMessage))
            {
                if (string.IsNullOrWhiteSpace(app) || app.Length > 260 || use.Seconds <= 0) continue;
                var key = (day, user, app.ToLowerInvariant());
                var cur = _usage.GetValueOrDefault(key);
                _usage[key] = (cur.Seconds + Math.Min(use.Seconds, MaxSecondsPerMessage), use.Product ?? cur.Product);
            }

            var old = _sessions.TryGetValue(user, out var s) ? s.State : SessionStates.Active;
            if (!_sessions.ContainsKey(user)) _sessions[user] = (1, old);
            var state = m.State is SessionStates.Active or SessionStates.Idle or SessionStates.Locked ? m.State : old;
            if (state == old) return;
            _sessions[user] = (_sessions[user].Connections, state);
            foreach (var e in Transition(old, state)) _events.Add(new(now, user, e));
        }
    }

    public static IEnumerable<string> Transition(string from, string to)
    {
        if (to == SessionStates.Locked) { yield return SessionEvents.Lock; yield break; }
        if (from == SessionStates.Locked) yield return SessionEvents.Unlock;
        if (to == SessionStates.Idle) yield return SessionEvents.Idle;
        else if (from == SessionStates.Idle) yield return SessionEvents.Active;
    }

    /// <summary>Everything collected since the last call, or null when there is nothing.</summary>
    public ActivityBatch? Drain()
    {
        lock (_lock)
        {
            if (_usage.Count == 0 && _events.Count == 0) return null;
            var batch = new ActivityBatch(
                _usage.Select(kv => new AppUsageItem(kv.Key.Day, kv.Key.User, kv.Key.App, kv.Value.Product, kv.Value.Seconds)).ToList(),
                [.. _events]);
            _usage.Clear();
            _events.Clear();
            return batch;
        }
    }

    /// <summary>Tracking switched off: forget anything not yet uploaded.</summary>
    public void Clear()
    {
        lock (_lock) { _usage.Clear(); _events.Clear(); }
    }
}
