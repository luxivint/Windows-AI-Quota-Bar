using QuotaBar.Core.Providers;

namespace QuotaBar.App;

/// <summary>
/// Fake data for <c>--demo</c>: used for README screenshots and for trying the UI without any account.
/// Nothing here touches real credentials, settings or the snapshot file.
/// </summary>
public static class DemoData
{
    public static readonly List<AntigravityAccounts.Account> AntigravityAccounts = new()
    {
        new("work@example.com", true),
        new("personal@example.com", false),
    };

    public static readonly List<string> OpenCodeKeys = new() { "OpenCode Go" };

    public static AppSettings Settings() => new()
    {
        ReadOnly = true,
        StartWithWindows = true,
        WidgetMetrics = new() { "claude|five_hour", "codex|primary" },
        // For screenshots: QUOTABAR_DEMO_WIDGET=bars,status,large
        WidgetStyle = DemoWidget("bars", "rings") ?? "rings",
        WidgetColor = DemoWidget("brand", "status", "mono", "accent") ?? "brand",
        WidgetSize = DemoWidget("compact", "normal", "large") ?? "normal",
        KeyAccounts = new() { new KeyAccount { Id = "demo0openrtr", Kind = "openrouter", Label = "OpenRouter" } },
    };

    static string? DemoWidget(params string[] options) =>
        (Environment.GetEnvironmentVariable("QUOTABAR_DEMO_WIDGET") ?? "").Split(',').FirstOrDefault(options.Contains);

    public static UsageReport Report()
    {
        var now = DateTimeOffset.Now;
        UsageWindow W(string key, string label, double used, TimeSpan resetsIn, long? len = null, string? detail = null) =>
            new() { Key = key, Label = label, UsedPercent = used, ResetsAt = now + resetsIn, WindowSeconds = len, Detail = detail };
        const long H5 = 18000, D7 = 604800, D30 = 2592000;

        ProviderSnapshot Agy(string email, string plan, double pro, double flash, double claude, double gpt, double img, bool active) => new()
        {
            Id = "antigravity:" + email, ProviderKind = "antigravity", DisplayName = "Antigravity", Account = email, Plan = plan,
            IsActiveAccount = active, DashboardUrl = "https://antigravity.google",
            Windows =
            {
                W("gemini-pro", "Gemini Pro", pro, TimeSpan.FromHours(3.4), detail: "Gemini 3 Pro"),
                W("gemini-flash", "Gemini Flash", flash, TimeSpan.FromHours(3.4), detail: "Gemini 3 Flash, Gemini 3.1 Flash Lite"),
                W("claude", "Claude", claude, TimeSpan.FromHours(1.2), detail: "Claude Sonnet, Claude Opus"),
                W("gpt", "GPT-OSS", gpt, TimeSpan.FromHours(4.6), detail: "GPT-OSS 120B"),
                W("image", "Image", img, TimeSpan.FromHours(4.6), detail: "Gemini Flash Image"),
            },
        };

        var report = new UsageReport
        {
            GeneratedAt = now,
            Providers =
            {
                new ProviderSnapshot
                {
                    Id = "claude", ProviderKind = "claude", DisplayName = "Claude", Account = "you@example.com", Plan = "Max 5x",
                    DashboardUrl = "https://claude.ai/settings/usage", StatusUrl = "https://status.anthropic.com",
                    Windows = { W("five_hour", "Session", 34, TimeSpan.FromHours(2.6), H5), W("seven_day", "Weekly", 58, TimeSpan.FromDays(2.4), D7) },
                },
                new ProviderSnapshot
                {
                    Id = "codex", ProviderKind = "codex", DisplayName = "Codex", Account = "you@example.com", Plan = "Plus",
                    DashboardUrl = "https://chatgpt.com/codex/settings/usage", StatusUrl = "https://status.openai.com",
                    Windows = { W("primary", "Session", 12, TimeSpan.FromHours(4.1), H5), W("secondary", "Weekly", 41, TimeSpan.FromDays(4.3), D7) },
                    Info = { new("Credits", "0") },
                },
                Agy("work@example.com", "Google AI Pro", 22, 8, 64, 5, 0, true),
                Agy("personal@example.com", "Google AI Plus", 47, 15, 88, 0, 12, false),
                new ProviderSnapshot
                {
                    Id = "opencode-go:demo", ProviderKind = "opencode-go", DisplayName = "OpenCode Go", Account = "OpenCode Go", Plan = "Go",
                    DashboardUrl = "https://opencode.ai/auth",
                    Windows =
                    {
                        W("rolling", "Session", 18, TimeSpan.FromHours(3.2), H5),
                        W("weekly", "Weekly", 46, TimeSpan.FromDays(3.1), D7),
                        W("monthly", "Monthly", 27, TimeSpan.FromDays(19), D30),
                    },
                },
                new ProviderSnapshot
                {
                    Id = "openrouter:demo0openrtr", ProviderKind = "openrouter", DisplayName = "OpenRouter", Account = "OpenRouter",
                    DashboardUrl = "https://openrouter.ai/activity",
                    Windows = { new UsageWindow { Key = "credits", Label = "Credits", UsedPercent = 36, Detail = "$12.80 of $20 left" } },
                    Info = { new("Today", "$0.42"), new("This week", "$3.15") },
                },
            },
        };
        report.Providers.RemoveAll(p => p.ProviderKind == "codex");
        report.Providers.InsertRange(1, CodexSnapshotMapper.Map(CodexAccountsPage.DemoState(), now));
        return report;
    }
    /// <summary>Plausible history for the demo charts: usage grows in bursts during the day and drops at each reset.</summary>
    public static CostSummary Cost()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var daily = Enumerable.Range(0, 30).Select(i =>
        {
            var d = today.AddDays(-29 + i);
            var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            return (d, weekend ? 1.8 + i % 3 : 9 + 6 * Math.Sin(i * 0.9) + i * 0.35);
        }).ToList();
        double Sum(int days) => daily.Skip(30 - days).Sum(x => x.Item2);
        var month = daily.Where(x => x.d.Month == today.Month).Sum(x => x.Item2);
        return new CostSummary(daily[^1].Item2, Sum(7), month, Sum(30), 412_000_000, 3_870,
            new List<(string, double)> { ("Opus 5.5", Sum(30) * 0.82), ("Sonnet 5.5", Sum(30) * 0.16), ("Haiku 5.5", Sum(30) * 0.02) }, daily, [], now);
    }

    public static List<HistoryPoint> History(string providerId, UsageWindow w, DateTimeOffset from, DateTimeOffset to)
    {
        var now = DateTimeOffset.Now;
        var rnd = new Random(providerId.GetHashCode() ^ w.Key.GetHashCode());
        var len = TimeSpan.FromSeconds(w.WindowSeconds ?? 86400);
        var reset = w.ResetsAt ?? now.AddHours(12);
        var list = new List<HistoryPoint>();
        // Walk cycles backwards from the current one.
        for (var end = reset; end - len > from.AddDays(-1); end -= len)
        {
            var start = end - len;
            var current = end == reset;
            var peak = current ? w.UsedPercent : 35 + rnd.NextDouble() * 60;
            var stop = current ? now : end;
            double used = 0;
            var steps = Math.Max(2, (int)((stop - start).TotalMinutes / 20));
            for (int i = 0; i <= steps; i++)
            {
                var t = start + (stop - start) * i / steps;
                if (t < from.AddHours(-1) || t > to) continue;
                var hour = t.ToLocalTime().Hour;
                var active = hour is >= 9 and <= 23;
                var target = peak * i / steps;
                if (active || current) used = Math.Max(used, Math.Min(peak, target + (rnd.NextDouble() - 0.5) * 4));
                if (current && i == steps) used = w.UsedPercent;
                list.Add(new HistoryPoint(t.ToUnixTimeSeconds(), providerId, w.Key, Math.Round(Math.Max(0, used), 1), end.ToUnixTimeSeconds()));
            }
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }
}
