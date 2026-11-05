using System.Diagnostics;
using System.Text.Json;

namespace QuotaBar.Core.Providers;

/// <summary>An account the user added with an API key / token (key itself lives DPAPI-encrypted in SecretStore).</summary>
public sealed class KeyAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string SecretName => "acct:" + Id;
}

/// <summary>Catalog entry for a provider that can be added from Settings.</summary>
public sealed record ProviderDef(
    string Kind, string Name, string Description, string KeyHint, string? KeyUrl, string? DashboardUrl, string? StatusUrl,
    bool Beta, Func<string, CancellationToken, Task<ProviderSnapshot>> Fetch, Func<string?>? AutoDetect = null);

/// <summary>
/// API-key based providers. Each one turns a key into a <see cref="ProviderSnapshot"/>.
/// Endpoints are the providers' own account/usage APIs.
/// </summary>
public static class KeyProviders
{
    public static readonly IReadOnlyList<ProviderDef> Catalog = new ProviderDef[]
    {
        new("openrouter", "OpenRouter", "Credits and key limit across 300+ models", "sk-or-…",
            "https://openrouter.ai/settings/keys", "https://openrouter.ai/activity", "https://status.openrouter.ai", false, OpenRouter),
        new("copilot", "GitHub Copilot", "Premium requests and chat quota", "GitHub token",
            "https://github.com/settings/tokens", "https://github.com/settings/copilot", "https://www.githubstatus.com", true, Copilot,
            () => DetectGh() != null ? GhCli : null),
        new("zai", "z.ai GLM Coding", "GLM Coding Plan: 5-hour prompts and monthly tool calls", "API key",
            "https://z.ai/manage-apikey/apikey-list", "https://z.ai/manage-apikey/subscription", null, true, Zai),
        new("kimi", "Kimi Code", "Kimi For Coding plan usage", "API key (sk-kimi-…)",
            "https://www.kimi.com/coding", "https://www.kimi.com/coding", null, true, KimiCode),
        new("minimax", "MiniMax Coding", "MiniMax Coding Plan prompts per window", "API key",
            "https://platform.minimax.io/user-center/basic-information/interface-key", "https://platform.minimax.io/user-center/payment/coding-plan", null, true, MiniMax),
        new("deepseek", "DeepSeek", "API balance", "sk-…",
            "https://platform.deepseek.com/api_keys", "https://platform.deepseek.com/usage", "https://status.deepseek.com", false, DeepSeek),
        new("moonshot", "Moonshot API", "Kimi API balance", "sk-…",
            "https://platform.moonshot.ai/console/api-keys", "https://platform.moonshot.ai/console/account", null, false, Moonshot),
    };

    public static ProviderDef? Find(string kind) => Catalog.FirstOrDefault(d => d.Kind == kind);

    // ---- account storage --------------------------------------------------------------------------

    public static List<KeyAccount> Accounts(AppSettings s) => s.KeyAccounts;

    public static KeyAccount Add(AppSettings s, string kind, string label, string key)
    {
        var acc = new KeyAccount { Kind = kind, Label = string.IsNullOrWhiteSpace(label) ? Find(kind)?.Name ?? kind : label.Trim() };
        SecretStore.Set(acc.SecretName, key.Trim());
        s.KeyAccounts.Add(acc);
        s.Save();
        return acc;
    }

    public static void Remove(AppSettings s, string id)
    {
        var acc = s.KeyAccounts.FirstOrDefault(a => a.Id == id);
        if (acc is null) return;
        SecretStore.Set(acc.SecretName, null);
        s.KeyAccounts.Remove(acc);
        s.Save();
    }

    /// <summary>Try a key once before saving it.</summary>
    public static Task<ProviderSnapshot> Test(string kind, string key, CancellationToken ct) =>
        (Find(kind) ?? throw new InvalidOperationException("Unknown provider")).Fetch(key.Trim(), ct);

    // ---- helpers -------------------------------------------------------------------------------------

    static ProviderSnapshot Snap(string kind, string name, string? plan = null) => new()
    {
        Id = kind, ProviderKind = kind, DisplayName = name, Plan = plan, Status = ProviderStatus.Ok,
        DashboardUrl = Find(kind)?.DashboardUrl, StatusUrl = Find(kind)?.StatusUrl,
    };

    static async Task<JsonElement> Get(string url, CancellationToken ct, string? bearer = null, Dictionary<string, string>? headers = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return await Http.SendJson(req, ct);
    }

    static DateTimeOffset? Epoch(double? v) => v is not { } x || x <= 0 ? null
        : x > 1e12 ? DateTimeOffset.FromUnixTimeMilliseconds((long)x).ToLocalTime() : DateTimeOffset.FromUnixTimeSeconds((long)x).ToLocalTime();

    static DateTimeOffset? AnyDate(JsonElement e, string name) =>
        e.Prop(name) is { } v ? v.ValueKind == JsonValueKind.Number ? Epoch(v.GetDouble()) : e.Date(name) : null;

    static string Money(double v, string cur = "$") => cur == "$" ? $"${v:0.00}" : $"{v:0.00} {cur}";

    // ---- OpenRouter: GET /api/v1/key (+ /api/v1/credits) -------------------------------------------

    static async Task<ProviderSnapshot> OpenRouter(string key, CancellationToken ct)
    {
        var s = Snap("openrouter", "OpenRouter");
        var k = (await Get("https://openrouter.ai/api/v1/key", ct, key)).Prop("data") ?? default;
        if (k.ValueKind == JsonValueKind.Object)
        {
            if (k.Prop("is_free_tier")?.ValueKind == JsonValueKind.True) s.Plan = "Free";
            if (k.Num("limit") is { } limit && limit > 0)
            {
                var remaining = k.Num("limit_remaining") ?? limit - (k.Num("usage") ?? 0);
                s.Windows.Add(new UsageWindow
                {
                    Key = "key_limit", Label = "Key limit" + (k.Str("limit_reset") is { } lr ? $" ({lr})" : ""),
                    UsedPercent = Math.Clamp((limit - remaining) / limit * 100, 0, 100), Detail = $"{Money(remaining)} of {Money(limit)} left",
                });
            }
            if (k.Num("usage_daily") is { } d) s.Info.Add(new("Today", Money(d)));
            if (k.Num("usage_weekly") is { } w) s.Info.Add(new("This week", Money(w)));
            if (k.Num("usage_monthly") is { } m) s.Info.Add(new("This month", Money(m)));
        }
        try
        {
            var c = (await Get("https://openrouter.ai/api/v1/credits", ct, key)).Prop("data");
            if (c is { } cr && cr.Num("total_credits") is { } total && total > 0)
            {
                var used = cr.Num("total_usage") ?? 0;
                s.Windows.Insert(0, new UsageWindow
                {
                    Key = "credits", Label = "Credits", UsedPercent = Math.Clamp(used / total * 100, 0, 100),
                    Detail = $"{Money(total - used)} of {Money(total)} left",
                });
                s.Info.Insert(0, new("Balance", Money(total - used)));
            }
        }
        catch (Http.HttpError) { /* credits endpoint can require a different key type */ }
        return s;
    }

    // ---- GitHub Copilot: GET api.github.com/copilot_internal/user ------------------------------------

    /// <summary>
    /// Stored instead of a token when the user picks "use my GitHub CLI login": the token is asked from
    /// `gh auth token` at each refresh and never copied into our storage.
    /// </summary>
    public const string GhCli = "@gh-cli";

    static string? DetectGh()
    {
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var t = p?.StandardOutput.ReadToEnd().Trim();
            p?.WaitForExit(5000);
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch { return null; }
    }

    static async Task<ProviderSnapshot> Copilot(string token, CancellationToken ct)
    {
        if (token == GhCli) token = DetectGh() ?? throw new InvalidOperationException("GitHub CLI is not signed in. Run `gh auth login`.");
        var j = await Get("https://api.github.com/copilot_internal/user", ct, headers: new()
        {
            ["Authorization"] = "token " + token,
            ["Editor-Version"] = "vscode/1.100.0",
            ["Editor-Plugin-Version"] = "copilot-chat/0.30.0",
            ["X-GitHub-Api-Version"] = "2025-04-01",
        });
        var plan = j.Str("copilot_plan") ?? j.Str("access_type_sku");
        var s = Snap("copilot", "GitHub Copilot", plan is null ? null : char.ToUpper(plan[0]) + plan[1..].Replace('_', ' '));
        var reset = AnyDate(j, "quota_reset_date_utc") ?? AnyDate(j, "quota_reset_date");
        if (j.Prop("quota_snapshots") is { ValueKind: JsonValueKind.Object } q)
        {
            foreach (var (key, label) in new[] { ("premium_interactions", "Premium requests"), ("chat", "Chat"), ("completions", "Completions") })
            {
                if (q.Prop(key) is not { } w || w.Prop("unlimited")?.ValueKind == JsonValueKind.True) continue;
                var pct = w.Num("percent_remaining") ?? (w.Num("entitlement") is { } e && e > 0 ? (w.Num("remaining") ?? 0) / e * 100 : 100);
                s.Windows.Add(new UsageWindow
                {
                    Key = key, Label = label, UsedPercent = Math.Clamp(100 - pct, 0, 100), ResetsAt = reset,
                    Detail = w.Num("entitlement") is { } ent ? $"{w.Num("remaining") ?? 0:0} of {ent:0} left" : null,
                });
            }
        }
        if (s.Windows.Count == 0) s.Info.Add(new("Quota", "Unlimited"));
        s.Account = j.Str("login");
        return s;
    }

    // ---- z.ai GLM Coding Plan: GET /api/monitor/usage/quota/limit -------------------------------------

    static async Task<ProviderSnapshot> Zai(string key, CancellationToken ct)
    {
        JsonElement j;
        try { j = await Get("https://api.z.ai/api/monitor/usage/quota/limit", ct, headers: new() { ["Authorization"] = key }); }
        catch (Http.HttpError e) when (e.Code is 401 or 403 or 404)
        {
            j = await Get("https://open.bigmodel.cn/api/monitor/usage/quota/limit", ct, headers: new() { ["Authorization"] = key });
        }
        var data = j.Prop("data") ?? j;
        var s = Snap("zai", "z.ai GLM Coding", data.Str("level") ?? data.Str("planName"));
        if (data.Prop("limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var type = l.Str("type") ?? "";
                var label = type.Contains("TIME") ? "Tool calls (monthly)" : type.Contains("TOKEN") ? "Prompts (5-hour)" : type;
                double used = l.Num("percentage") ?? (l.Num("usage") is { } u && u > 0 ? (l.Num("currentValue") ?? 0) / u * 100 : 0);
                s.Windows.Add(new UsageWindow
                {
                    Key = type.ToLowerInvariant(), Label = label, UsedPercent = Math.Clamp(used, 0, 100),
                    ResetsAt = AnyDate(l, "nextResetTime"), WindowSeconds = type.Contains("TOKEN") ? 18000 : null,
                    Detail = l.Num("usage") is { } total ? $"{l.Num("currentValue") ?? 0:0} / {total:0}" : null,
                });
            }
        }
        return s;
    }

    // ---- Kimi For Coding: GET api.kimi.com/coding/v1/usages ------------------------------------------

    static async Task<ProviderSnapshot> KimiCode(string key, CancellationToken ct)
    {
        var j = await Get("https://api.kimi.com/coding/v1/usages", ct, key);
        var s = Snap("kimi", "Kimi Code");
        void Add(string k, string label, JsonElement d, long? secs)
        {
            var limit = d.Num("limit") ?? 0;
            var remaining = d.Num("remaining") ?? (limit - (d.Num("used") ?? 0));
            if (limit <= 0) return;
            s.Windows.Add(new UsageWindow
            {
                Key = k, Label = label, UsedPercent = Math.Clamp((limit - remaining) / limit * 100, 0, 100),
                ResetsAt = AnyDate(d, "resetTime") ?? AnyDate(d, "reset_time"), WindowSeconds = secs, Detail = $"{remaining:0} of {limit:0} left",
            });
        }
        if (j.Prop("limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var w = l.Prop("window");
                var dur = w?.Num("duration") ?? 0;
                var unit = w?.Str("timeUnit") ?? "";
                long? secs = unit.Contains("MINUTE") ? (long)(dur * 60) : unit.Contains("HOUR") ? (long)(dur * 3600) : unit.Contains("DAY") ? (long)(dur * 86400) : null;
                var label = secs is { } x ? CodexProvider.WindowName(x, "Window") : "Window";
                if (l.Prop("detail") is { } d) Add("w" + secs, label, d, secs);
            }
        }
        if (j.Prop("usage") is { ValueKind: JsonValueKind.Object } total) Add("total", "Weekly", total, 604800);
        return s;
    }

    // ---- MiniMax Coding Plan: GET /v1/api/openplatform/coding_plan/remains ----------------------------

    static async Task<ProviderSnapshot> MiniMax(string key, CancellationToken ct)
    {
        JsonElement j;
        try { j = await Get("https://api.minimax.io/v1/api/openplatform/coding_plan/remains", ct, key); }
        catch (Http.HttpError e) when (e.Code is 401 or 403 or 404) { j = await Get("https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains", ct, key); }
        var s = Snap("minimax", "MiniMax Coding");
        if (j.Prop("model_remains") is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var m in arr.EnumerateArray().Take(4))
            {
                var name = m.Str("model_name") ?? "Model";
                var total = m.Num("current_interval_total_count") ?? 0;
                // MiniMax reports the REMAINING count in *_usage_count.
                var remaining = m.Num("current_interval_usage_count") ?? total;
                if (total <= 0) continue;
                var start = AnyDate(m, "start_time"); var end = AnyDate(m, "end_time");
                s.Windows.Add(new UsageWindow
                {
                    Key = name.ToLowerInvariant(), Label = name, UsedPercent = Math.Clamp((total - remaining) / total * 100, 0, 100),
                    ResetsAt = end, WindowSeconds = start is { } a && end is { } b ? (long)(b - a).TotalSeconds : null,
                    Detail = $"{remaining:0} of {total:0} prompts left",
                });
            }
        }
        return s;
    }

    // ---- DeepSeek: GET /user/balance --------------------------------------------------------------------

    static async Task<ProviderSnapshot> DeepSeek(string key, CancellationToken ct)
    {
        var j = await Get("https://api.deepseek.com/user/balance", ct, key);
        var s = Snap("deepseek", "DeepSeek", "API");
        if (j.Prop("balance_infos") is { ValueKind: JsonValueKind.Array } arr)
            foreach (var b in arr.EnumerateArray())
            {
                var cur = b.Str("currency") ?? "";
                var sym = cur == "USD" ? "$" : cur;
                s.Info.Add(new("Balance", Money(b.Num("total_balance") ?? 0, sym)));
                if (b.Num("granted_balance") is { } g && g > 0) s.Info.Add(new("Granted", Money(g, sym)));
                if (b.Num("topped_up_balance") is { } t) s.Info.Add(new("Topped up", Money(t, sym)));
            }
        if (j.Prop("is_available")?.ValueKind == JsonValueKind.False) s.Message = "Balance too low to make requests";
        return s;
    }

    // ---- Moonshot: GET /v1/users/me/balance -------------------------------------------------------------

    static async Task<ProviderSnapshot> Moonshot(string key, CancellationToken ct)
    {
        JsonElement j;
        try { j = await Get("https://api.moonshot.ai/v1/users/me/balance", ct, key); }
        catch (Http.HttpError e) when (e.Code is 401 or 403) { j = await Get("https://api.moonshot.cn/v1/users/me/balance", ct, key); }
        var d = j.Prop("data") ?? j;
        var s = Snap("moonshot", "Moonshot API", "API");
        if (d.Num("available_balance") is { } a) s.Info.Add(new("Available", Money(a)));
        if (d.Num("voucher_balance") is { } v && v > 0) s.Info.Add(new("Voucher", Money(v)));
        if (d.Num("cash_balance") is { } c) s.Info.Add(new("Cash", Money(c)));
        return s;
    }
}

/// <summary>One IUsageProvider per catalog entry, reporting every key-account of that kind.</summary>
public sealed class KeyAccountsProvider(ProviderDef def) : IUsageProvider
{
    public string Kind => def.Kind;
    public string Name => def.Name;

    public async Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        var accounts = AppSettings.Load().KeyAccounts.Where(a => a.Kind == def.Kind).ToList();
        var results = await Task.WhenAll(accounts.Select(async a =>
        {
            ProviderSnapshot s;
            try
            {
                var key = SecretStore.Get(a.SecretName) ?? throw new InvalidOperationException("Key missing; add the account again.");
                s = await def.Fetch(key, ct);
            }
            catch (Http.HttpError e) when (e.Code is 401 or 403)
            {
                s = ProviderSnapshot.Fail(def.Kind, def.Kind, def.Name, ProviderStatus.NeedsLogin, "Key rejected. Check it or create a new one.");
            }
            catch (Exception e)
            {
                s = ProviderSnapshot.Fail(def.Kind, def.Kind, def.Name, ProviderStatus.Error, e.Message);
            }
            s.Id = $"{def.Kind}:{a.Id}";
            s.ProviderKind = def.Kind;
            s.DisplayName = def.Name;
            s.Account ??= a.Label;
            s.DashboardUrl ??= def.DashboardUrl;
            s.StatusUrl ??= def.StatusUrl;
            return s;
        }));
        return results;
    }
}
