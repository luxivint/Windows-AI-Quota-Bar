using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaBar.Core;

/// <summary>Anthropic API list prices in USD per million tokens. Cache writes: 5-minute 1.25× input, 1-hour 2× input.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead);

public static class Pricing
{
    // Longest prefix wins. Source: Anthropic first-party API price list (2026-10).
    static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", new(10, 50, 0.25)),
        ("claude-mythos-5-1", new(10, 50, 0.25)),
        ("claude-fable-5", new(10, 50, 1.00)),
        ("claude-mythos-5", new(10, 50, 1.00)),
        ("claude-opus-5-5", new(4, 20, 0.20)),
        ("claude-opus-5", new(5, 25, 0.50)),
        ("claude-opus-4-8", new(5, 25, 0.50)),
        ("claude-opus-4-7", new(5, 25, 0.50)),
        ("claude-opus-4-6", new(5, 25, 0.50)),
        ("claude-opus-4-5", new(5, 25, 0.50)),
        ("claude-opus-4", new(15, 75, 1.50)),
        ("claude-sonnet-5-5", new(2, 10, 0.20)),
        ("claude-sonnet-5", new(2, 10, 0.20)),
        ("claude-sonnet-4", new(3, 15, 0.30)),
        ("claude-3-7-sonnet", new(3, 15, 0.30)),
        ("claude-haiku-5-5", new(0.10, 0.50, 0.01)),
        ("claude-haiku-4-5", new(1, 5, 0.10)),
        ("claude-3-5-haiku", new(0.80, 4, 0.08)),
    ];

    public static ModelPrice? Find(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        if (m.StartsWith("anthropic.")) m = m["anthropic.".Length..];
        ModelPrice? best = null; var len = 0;
        foreach (var (p, price) in Table)
            if (m.StartsWith(p, StringComparison.Ordinal) && p.Length > len) { best = price; len = p.Length; }
        return best;
    }

    /// <summary>"claude-opus-5-5-20260101" → "Opus 5.5".</summary>
    public static string Display(string model)
    {
        var parts = model.Replace("claude-", "").Split('-');
        if (parts.Length == 0) return model;
        var family = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var ver = string.Join(".", parts.Skip(1).TakeWhile(x => x.Length <= 2 && x.All(char.IsDigit)));
        return ver.Length > 0 ? $"{family} {ver}" : family;
    }
}

public sealed class CostBucket
{
    [JsonPropertyName("c")] public double Cost { get; set; }
    [JsonPropertyName("i")] public long Input { get; set; }
    [JsonPropertyName("o")] public long Output { get; set; }
    [JsonPropertyName("w")] public long CacheWrite { get; set; }
    [JsonPropertyName("r")] public long CacheRead { get; set; }
    [JsonPropertyName("n")] public int Requests { get; set; }

    public void Add(CostBucket b) { Cost += b.Cost; Input += b.Input; Output += b.Output; CacheWrite += b.CacheWrite; CacheRead += b.CacheRead; Requests += b.Requests; }
    [JsonIgnore] public long Tokens => Input + Output + CacheWrite + CacheRead;
}

public sealed record CostSummary(
    double Today, double Last7, double ThisMonth, double Last30,
    long TokensLast30, int RequestsLast30,
    IReadOnlyList<(string Model, double Cost)> ByModelLast30,
    IReadOnlyList<(DateOnly Day, double Cost)> Daily,
    IReadOnlyList<string> UnpricedModels, DateTimeOffset ScannedAt);

/// <summary>
/// Estimates what Claude Code usage would cost at API list prices, from the local session logs
/// (~/.claude/projects/**/*.jsonl). Reads only token counts and model names; files are scanned
/// incrementally and the per-day totals are kept in cost-cache.json so deleted logs still count.
/// </summary>
public static class CostScan
{
    public const int KeepDays = 95;

    sealed class FileState
    {
        [JsonPropertyName("off")] public long Offset { get; set; }
        [JsonPropertyName("mt")] public long Modified { get; set; }
        /// <summary>"yyyy-MM-dd|model" → totals.</summary>
        [JsonPropertyName("d")] public Dictionary<string, CostBucket> Days { get; set; } = new();
    }

    sealed class Cache
    {
        [JsonPropertyName("v")] public int Version { get; set; } = 1;
        [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new();
        [JsonPropertyName("unpriced")] public HashSet<string> Unpriced { get; set; } = new();
        /// <summary>Response id → day number. Resumed and forked sessions copy earlier responses into new files.</summary>
        [JsonPropertyName("seen")] public Dictionary<string, int> Seen { get; set; } = new();
    }

    public static string CachePath => Path.Combine(AppPaths.Data, "cost-cache.json");
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static IEnumerable<string> DefaultRoots()
    {
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            foreach (var d in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(d, "projects");
        else
        {
            yield return Path.Combine(AppPaths.Home, ".claude", "projects");
            yield return Path.Combine(AppPaths.Home, ".config", "claude", "projects");
        }
    }

    public static async Task<CostSummary> RunAsync(IEnumerable<string>? roots = null, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await Task.Run(() => Run(roots ?? DefaultRoots(), now ?? DateTimeOffset.Now, ct), ct); }
        finally { Gate.Release(); }
    }

    static CostSummary Run(IEnumerable<string> roots, DateTimeOffset now, CancellationToken ct)
    {
        var cache = Json.Read<Cache>(CachePath) ?? new Cache();
        var cutoff = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-KeepDays);
        var oldestFile = now.UtcDateTime.AddDays(-KeepDays);

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTimeUtc < oldestFile) continue;
                    var key = path.ToLowerInvariant();
                    if (!cache.Files.TryGetValue(key, out var st)) cache.Files[key] = st = new FileState();
                    if (fi.Length < st.Offset) { st.Offset = 0; st.Days.Clear(); } // rewritten
                    if (fi.Length == st.Offset) continue;
                    ReadFrom(path, st, cache);
                    st.Modified = fi.LastWriteTimeUtc.Ticks;
                }
                catch (IOException) { } // in use or vanished: next time
                catch (UnauthorizedAccessException) { }
            }
        }

        // Drop old days and files that no longer contribute.
        foreach (var (k, st) in cache.Files.ToList())
        {
            foreach (var d in st.Days.Keys.Where(d => DateOnly.TryParse(d.Split('|')[0], out var day) && day < cutoff).ToList()) st.Days.Remove(d);
            if (st.Days.Count == 0 && !File.Exists(k)) cache.Files.Remove(k);
        }
        foreach (var (id, day) in cache.Seen.ToList()) if (day < cutoff.DayNumber) cache.Seen.Remove(id);
        try { Json.WriteAtomic(CachePath, cache); } catch { }
        return Summarize(cache, now);
    }

    static void ReadFrom(string path, FileState st, Cache cache)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(st.Offset, SeekOrigin.Begin);
        var buf = new byte[1 << 16];
        var line = new MemoryStream();
        long pos = st.Offset, lineStart = st.Offset;
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < n; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                line.Write(buf, start, i - start);
                ParseLine(line.GetBuffer().AsSpan(0, (int)line.Length), st, cache);
                line.SetLength(0);
                start = i + 1;
                lineStart = pos + i + 1;
            }
            line.Write(buf, start, n - start);
            pos += n;
        }
        st.Offset = lineStart; // a partial last line is read again next time
    }

    static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();

    static void ParseLine(ReadOnlySpan<byte> line, FileState st, Cache cache)
    {
        if (line.IndexOf(UsageMarker) < 0) return; // cheap skip for user/tool lines
        try
        {
            var r = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref r);
            var e = doc.RootElement;
            if (e.Str("type") != "assistant" || e.Prop("message") is not { ValueKind: JsonValueKind.Object } msg) return;
            if (msg.Prop("usage") is not { ValueKind: JsonValueKind.Object } u) return;
            var model = msg.Str("model");
            if (model is null or "<synthetic>") return;
            if (e.Date("timestamp") is not { } ts) return;
            var day = DateOnly.FromDateTime(ts.ToLocalTime().DateTime);
            // Claude Code writes one line per content block, and resumed sessions copy old lines: count each response once.
            var id = msg.Str("id") ?? e.Str("requestId");
            if (id != null && !cache.Seen.TryAdd(id, day.DayNumber)) return;

            long L(JsonElement o, string k) => (long)(o.Num(k) ?? 0);
            var input = L(u, "input_tokens");
            var output = L(u, "output_tokens");
            var read = L(u, "cache_read_input_tokens");
            long w5, w1h;
            if (u.Prop("cache_creation") is { ValueKind: JsonValueKind.Object } cc)
            { w5 = L(cc, "ephemeral_5m_input_tokens"); w1h = L(cc, "ephemeral_1h_input_tokens"); }
            else { w5 = L(u, "cache_creation_input_tokens"); w1h = 0; }

            var bucket = new CostBucket { Input = input, Output = output, CacheRead = read, CacheWrite = w5 + w1h, Requests = 1 };
            if (Pricing.Find(model) is { } p)
            {
                var cost = (input * p.Input + output * p.Output + read * p.CacheRead + w5 * p.Input * 1.25 + w1h * p.Input * 2) / 1_000_000;
                if (u.Str("speed") == "fast") cost *= 2;
                bucket.Cost = cost;
            }
            else cache.Unpriced.Add(model);

            var dayKey = day.ToString("yyyy-MM-dd") + "|" + model;
            if (st.Days.TryGetValue(dayKey, out var b)) b.Add(bucket); else st.Days[dayKey] = bucket;
        }
        catch (JsonException) { }
    }

    static CostSummary Summarize(Cache cache, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var byDay = new Dictionary<DateOnly, double>();
        var byModel = new Dictionary<string, double>();
        double t = 0, d7 = 0, month = 0, d30 = 0; long tokens = 0; int reqs = 0;
        foreach (var st in cache.Files.Values)
            foreach (var (k, b) in st.Days)
            {
                var parts = k.Split('|', 2);
                if (!DateOnly.TryParse(parts[0], out var day) || day > today) continue;
                var age = today.DayNumber - day.DayNumber;
                if (age == 0) t += b.Cost;
                if (age < 7) d7 += b.Cost;
                if (day >= monthStart) month += b.Cost;
                if (age < 30)
                {
                    d30 += b.Cost; tokens += b.Tokens; reqs += b.Requests;
                    byDay[day] = byDay.GetValueOrDefault(day) + b.Cost;
                    var name = Pricing.Display(parts[1]);
                    byModel[name] = byModel.GetValueOrDefault(name) + b.Cost;
                }
            }
        var daily = Enumerable.Range(0, 30).Select(i => today.AddDays(-29 + i)).Select(d => (d, byDay.GetValueOrDefault(d))).ToList();
        return new CostSummary(t, d7, month, d30, tokens, reqs,
            byModel.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList(), daily,
            cache.Unpriced.OrderBy(x => x).ToList(), now);
    }

    /// <summary>"11.6B" / "45M" / "12K".</summary>
    public static string Tokens(long n)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return n >= 1_000_000_000 ? (n / 1e9).ToString("0.#", ci) + "B"
            : n >= 1_000_000 ? (n / 1e6).ToString("0.#", ci) + "M"
            : n >= 1_000 ? (n / 1e3).ToString("0", ci) + "K" : n.ToString(ci);
    }

    /// <summary>"$1,234" / "$12.40" / "$0.31".</summary>
    public static string Money(double usd) =>
        usd >= 1000 ? "$" + usd.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)
        : "$" + usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
