namespace QuotaBar.Core;

public enum AlertKind
{
    /// <summary>Usage crossed one of the thresholds (Value = threshold, % used).</summary>
    Threshold,
    /// <summary>At the current pace the window runs out before it resets (At = projected run-out).</summary>
    RunOut,
    /// <summary>A window that was well used has reset to (almost) zero.</summary>
    Reset,
    /// <summary>A long window resets soon with a lot left unused (Value = % left, At = reset).</summary>
    Unused,
}

public sealed record Alert(AlertKind Kind, ProviderSnapshot Provider, UsageWindow Window, double Value, DateTimeOffset? At);

public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Notify when this much (%) of a window is used.</summary>
    public List<int> Thresholds { get; set; } = new() { 80, 95 };
    public bool RunOut { get; set; } = true;
    public bool Resets { get; set; } = true;
    public bool Unused { get; set; } = true;
    /// <summary>Provider kinds that never notify ("claude", "codex", ...).</summary>
    public List<string> Muted { get; set; } = new();
}

/// <summary>
/// Decides which notifications a new report deserves. Each alert fires at most once per window cycle:
/// <see cref="AlertState"/> remembers the reset time of the cycle it fired in.
/// </summary>
public static class Alerts
{
    /// <summary>Unused-quota warning: windows of at least a day, resetting within this time.</summary>
    public static readonly TimeSpan UnusedLead = TimeSpan.FromHours(6);
    public const double UnusedMinLeft = 30;
    /// <summary>Run-out warning only when it is this close.</summary>
    public static readonly TimeSpan RunOutLead = TimeSpan.FromHours(1);

    public static List<Alert> Evaluate(UsageReport? previous, UsageReport current, AlertSettings s, AlertState state, DateTimeOffset now)
    {
        var list = new List<Alert>();
        if (!s.Enabled) return list;
        foreach (var p in current.Providers.Where(p => p.Status == ProviderStatus.Ok))
        {
            if (s.Muted.Contains(p.ProviderKind)) continue;
            // Only fresh data: a value carried over from the cache is not news.
            if (now - p.FetchedAt > TimeSpan.FromMinutes(20)) continue;
            var prevP = previous?.Providers.FirstOrDefault(x => x.Id == p.Id && x.Status == ProviderStatus.Ok);
            foreach (var w in p.Windows)
            {
                var key = p.Id + "|" + w.Key;
                var prevW = prevP?.Windows.FirstOrDefault(x => x.Key == w.Key);

                // Reset: was clearly used, now (almost) empty, and the previous reading is recent enough to compare.
                if (s.Resets && prevW is { UsedPercent: >= 50 } && w.UsedPercent <= 5
                    && prevP!.FetchedAt > now - TimeSpan.FromSeconds(Math.Max(w.WindowSeconds ?? 0, 6 * 3600)))
                {
                    if (state.TryFire(key + "|reset", w.ResetsAt, now)) list.Add(new Alert(AlertKind.Reset, p, w, w.UsedPercent, w.ResetsAt));
                    continue;
                }

                // Highest threshold crossed (one notification, not one per threshold).
                var crossed = s.Thresholds.Where(t => w.UsedPercent >= t).DefaultIfEmpty(0).Max();
                if (crossed > 0 && state.TryFire($"{key}|t{crossed}", w.ResetsAt, now))
                {
                    // Lower thresholds of the same cycle are implied.
                    foreach (var t in s.Thresholds.Where(t => t < crossed)) state.TryFire($"{key}|t{t}", w.ResetsAt, now);
                    list.Add(new Alert(AlertKind.Threshold, p, w, crossed, w.ResetsAt));
                    continue;
                }

                if (s.RunOut && w.UsedPercent < 95 && w.ProjectedRunOut(now) is { } ro && ro - now <= RunOutLead && ro - now > TimeSpan.Zero
                    && state.TryFire(key + "|runout", w.ResetsAt, now))
                {
                    list.Add(new Alert(AlertKind.RunOut, p, w, w.UsedPercent, ro));
                    continue;
                }

                if (s.Unused && w.WindowSeconds >= 86400 && w.ResetsAt is { } r && r > now && r - now <= UnusedLead
                    && w.LeftPercent >= UnusedMinLeft && state.TryFire(key + "|unused", w.ResetsAt, now))
                    list.Add(new Alert(AlertKind.Unused, p, w, w.LeftPercent, r));
            }
        }
        state.Prune(now);
        return list;
    }
}

/// <summary>Remembers which alerts fired in which window cycle. Persisted to alerts.json.</summary>
public sealed class AlertState
{
    /// <summary>alert key → reset time of the cycle it fired in (or the time it fired, when the window has no reset).</summary>
    public Dictionary<string, DateTimeOffset> Fired { get; set; } = new();

    /// <summary>True (and recorded) the first time an alert fires in this cycle.</summary>
    public bool TryFire(string key, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        var cycle = resetsAt ?? now;
        if (Fired.TryGetValue(key, out var prev))
        {
            // Same cycle: reset times drift by seconds/minutes between fetches.
            if (resetsAt is null ? now - prev < TimeSpan.FromHours(24) : Math.Abs((prev - cycle).TotalMinutes) < 60) return false;
        }
        Fired[key] = cycle;
        return true;
    }

    public void Prune(DateTimeOffset now)
    {
        foreach (var k in Fired.Where(kv => now - kv.Value > TimeSpan.FromDays(8)).Select(kv => kv.Key).ToList()) Fired.Remove(k);
    }

    public static string Path => System.IO.Path.Combine(AppPaths.Data, "alerts.json");
    public static AlertState Load() => Json.Read<AlertState>(Path) ?? new AlertState();
    public void Save() { try { Json.WriteAtomic(Path, this); } catch { } }
}
