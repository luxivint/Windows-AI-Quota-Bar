using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaBar.Core;

/// <summary>One recorded reading of a usage window.</summary>
public readonly record struct HistoryPoint(
    [property: JsonPropertyName("t")] long Time,
    [property: JsonPropertyName("id")] string ProviderId,
    [property: JsonPropertyName("k")] string WindowKey,
    [property: JsonPropertyName("u")] double Used,
    [property: JsonPropertyName("r")] long? ResetsAt)
{
    [JsonIgnore] public DateTimeOffset At => DateTimeOffset.FromUnixTimeSeconds(Time);
}

/// <summary>
/// Usage history for charts and forecasts: one JSON line per reading in
/// %LOCALAPPDATA%\QuotaBar\history\yyyy-MM.jsonl. Only usage percentages and reset times are kept
/// (no tokens, no account details beyond the provider id). Files older than <see cref="KeepMonths"/> are deleted.
/// </summary>
public static class History
{
    public const int KeepMonths = 4;
    /// <summary>A reading is written when the value changed, or at least this often to keep the line continuous.</summary>
    static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(30);
    static readonly object Gate = new();
    static Dictionary<string, HistoryPoint>? _last;

    public static string Dir
    {
        get { var d = Path.Combine(AppPaths.Data, "history"); Directory.CreateDirectory(d); return d; }
    }

    static string FileFor(DateTimeOffset t) => Path.Combine(Dir, t.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static void Record(UsageReport report) => Record(report, DateTimeOffset.UtcNow);

    public static void Record(UsageReport report, DateTimeOffset now)
    {
        lock (Gate)
        {
            _last ??= LoadLast(now);
            var lines = new List<string>();
            foreach (var p in report.Providers.Where(p => p.Status == ProviderStatus.Ok))
                foreach (var w in p.Windows)
                {
                    // Only fresh readings: a cached snapshot carried over from an earlier refresh is not new data.
                    if (now - p.FetchedAt > TimeSpan.FromMinutes(20)) continue;
                    var key = p.Id + "|" + w.Key;
                    var point = new HistoryPoint(p.FetchedAt.ToUnixTimeSeconds(), p.Id, w.Key, Math.Round(w.UsedPercent, 1), w.ResetsAt?.ToUnixTimeSeconds());
                    if (_last.TryGetValue(key, out var prev)
                        && Math.Abs(prev.Used - point.Used) < 0.5
                        && point.Time - prev.Time < Heartbeat.TotalSeconds) continue;
                    if (prev.Time == point.Time && prev.ProviderId != null) continue;
                    _last[key] = point;
                    lines.Add(JsonSerializer.Serialize(point));
                }
            if (lines.Count == 0) return;
            try
            {
                using var fs = new FileStream(FileFor(now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                foreach (var l in lines) sw.WriteLine(l);
            }
            catch (IOException) { /* next refresh writes again */ }
            Prune(now);
        }
    }

    /// <summary>Readings of one window since <paramref name="since"/>, oldest first.</summary>
    public static List<HistoryPoint> Load(string providerId, string windowKey, DateTimeOffset since)
    {
        var result = new List<HistoryPoint>();
        var start = new DateTimeOffset(since.UtcDateTime.Year, since.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (var m = start; m <= DateTimeOffset.UtcNow; m = m.AddMonths(1))
        {
            var f = FileFor(m);
            if (!File.Exists(f)) continue;
            foreach (var line in ReadLines(f))
            {
                if (!line.Contains(windowKey, StringComparison.Ordinal)) continue;
                HistoryPoint p;
                try { p = JsonSerializer.Deserialize<HistoryPoint>(line); } catch { continue; }
                if (p.ProviderId == providerId && p.WindowKey == windowKey && p.At >= since) result.Add(p);
            }
        }
        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    static IEnumerable<string> ReadLines(string path)
    {
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            text = sr.ReadToEnd();
        }
        catch (IOException) { yield break; }
        foreach (var l in text.Split('\n')) if (l.Length > 2) yield return l.TrimEnd('\r');
    }

    static Dictionary<string, HistoryPoint> LoadLast(DateTimeOffset now)
    {
        var d = new Dictionary<string, HistoryPoint>();
        foreach (var f in new[] { FileFor(now.AddMonths(-1)), FileFor(now) }.Distinct())
        {
            if (!File.Exists(f)) continue;
            foreach (var line in ReadLines(f))
            {
                try { var p = JsonSerializer.Deserialize<HistoryPoint>(line); d[p.ProviderId + "|" + p.WindowKey] = p; } catch { }
            }
        }
        return d;
    }

    static DateTimeOffset _lastPrune;
    static void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromHours(12)) return;
        _lastPrune = now;
        var keep = FileFor(now.AddMonths(-KeepMonths));
        foreach (var f in Directory.GetFiles(Dir, "*.jsonl"))
            if (string.CompareOrdinal(Path.GetFileName(f), Path.GetFileName(keep)) < 0)
                try { File.Delete(f); } catch { }
    }

    /// <summary>Test hook: forget the in-memory "last written" cache.</summary>
    public static void ResetCache() { lock (Gate) _last = null; }
}
