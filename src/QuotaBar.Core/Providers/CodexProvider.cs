using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaBar.Core.Providers;

/// <summary>ChatGPT / Codex: reads ~/.codex/auth.json (written by `codex login`) and calls the same usage endpoint Codex uses.</summary>
public sealed class CodexProvider : IUsageProvider
{
    public string Kind => "codex";
    public string Name => "Codex";

    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann"; // public Codex CLI OAuth client
    static string AuthPath => Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(AppPaths.Home, ".codex"), "auth.json");

    public async Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        if (!File.Exists(AuthPath))
            return new[] { ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin, "Run `codex login` once, then refresh.") };

        var text = await File.ReadAllTextAsync(AuthPath, ct);
        var auth = JsonNode.Parse(text)!.AsObject();
        var tokens = auth["tokens"]?.AsObject();
        var access = tokens?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(access))
            return new[] { ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin, "Codex is using an API key, not a ChatGPT login. Run `codex login`.") };

        JsonElement usage;
        try { usage = await GetUsage(tokens!, ct); }
        catch (Http.HttpError e) when (e.Code is 401 or 403)
        {
            // Token expired: refresh it without trampling a refresh the Codex CLI may be doing at the same time.
            var fresh = await TokenFile.RefreshAsync(AuthPath, text, current => Refresh(current, ct), ct);
            usage = await GetUsage(JsonNode.Parse(fresh)!["tokens"]!.AsObject(), ct);
        }
        return new[] { Parse(usage) };
    }

    static async Task<JsonElement> GetUsage(JsonObject tokens, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new("Bearer", tokens["access_token"]!.GetValue<string>());
        if (tokens["account_id"]?.GetValue<string>() is { } acc) req.Headers.Add("ChatGPT-Account-Id", acc);
        return await Http.SendJson(req, ct);
    }

    /// <summary>Returns the auth.json content with refreshed tokens (all other fields untouched).</summary>
    static async Task<string> Refresh(string fileText, CancellationToken ct)
    {
        var auth = JsonNode.Parse(fileText)!.AsObject();
        var tokens = auth["tokens"]!.AsObject();
        var rt = tokens["refresh_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Codex session expired. Run `codex login`.");
        var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.openai.com/oauth/token")
        {
            Content = JsonContent.Create(new { client_id = ClientId, grant_type = "refresh_token", refresh_token = rt, scope = "openid profile email" }),
        };
        JsonElement r;
        try { r = await Http.SendJson(req, ct); }
        catch (Http.HttpError) { throw new InvalidOperationException("Codex session expired. Run `codex login`."); }

        if (r.Str("access_token") is { } a) tokens["access_token"] = a;
        if (r.Str("refresh_token") is { } rr) tokens["refresh_token"] = rr;
        if (r.Str("id_token") is { } id) tokens["id_token"] = id;
        auth["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
        // Written back (by TokenFile) so the Codex CLI keeps working with the rotated refresh token.
        return auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    ProviderSnapshot Parse(JsonElement u)
    {
        var plan = u.Str("plan_type");
        var s = new ProviderSnapshot
        {
            Id = Kind, ProviderKind = Kind, DisplayName = Name, Status = ProviderStatus.Ok,
            Account = u.Str("email"),
            Plan = plan is null ? null : char.ToUpper(plan[0]) + plan[1..],
            DashboardUrl = "https://chatgpt.com/codex/settings/usage",
            StatusUrl = "https://status.openai.com",
        };
        if (u.Prop("rate_limit") is { } rl) AddWindows(s, rl, "", "");

        if (u.Prop("additional_rate_limits") is { ValueKind: JsonValueKind.Array } extra)
        {
            foreach (var item in extra.EnumerateArray())
            {
                var name = item.Str("limit_name") ?? item.Str("name") ?? item.Str("metered_feature") ?? "Extra";
                var inner = item.Prop("rate_limit") ?? item;
                AddWindows(s, inner, name + " ", name.ToLowerInvariant().Replace(' ', '_') + ":");
            }
        }

        if (u.Prop("credits") is { } cr)
        {
            var unlimited = cr.Prop("unlimited")?.ValueKind == JsonValueKind.True;
            var bal = cr.Num("balance") ?? 0;
            s.Info.Add(new("Credits", unlimited ? "Unlimited" : $"{bal:0.##}"));
        }
        if (u.Prop("rate_limit")?.Prop("limit_reached")?.ValueKind == JsonValueKind.True)
            s.Message = "Limit reached";
        return s;
    }

    static void AddWindows(ProviderSnapshot s, JsonElement rl, string labelPrefix, string keyPrefix)
    {
        foreach (var (prop, fallback) in new[] { ("primary_window", "Session"), ("secondary_window", "Weekly") })
        {
            if (rl.Prop(prop) is not { } w) continue;
            var len = (long?)w.Num("limit_window_seconds");
            var resetAt = w.Num("reset_at") is { } ra ? DateTimeOffset.FromUnixTimeSeconds((long)ra).ToLocalTime()
                        : w.Num("reset_after_seconds") is { } rs ? DateTimeOffset.Now.AddSeconds(rs) : (DateTimeOffset?)null;
            s.Windows.Add(new UsageWindow
            {
                Key = keyPrefix + (prop == "primary_window" ? "primary" : "secondary"),
                Label = (labelPrefix + WindowName(len, fallback)).Trim(),
                UsedPercent = w.Num("used_percent") ?? 0,
                ResetsAt = resetAt,
                WindowSeconds = len,
            });
        }
    }

    internal static string WindowName(long? seconds, string fallback) => seconds switch
    {
        18000 => "Session",
        604800 => "Weekly",
        86400 => "Daily",
        { } x when x % 3600 == 0 && x < 86400 => $"{x / 3600}-hour",
        _ => fallback,
    };
}
