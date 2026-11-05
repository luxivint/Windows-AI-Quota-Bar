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
        KeyAccounts = new() { new KeyAccount { Id = "demo0openrtr", Kind = "openrouter", Label = "OpenRouter" } },
    };

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

        return new UsageReport
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
    }
}
