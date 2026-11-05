using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaBar.Core.Providers;

/// <summary>
/// Claude (Pro / Max). Two sources, first that works wins:
///  1. claude.ai session (signed in through QuotaBar's own login window, stored DPAPI-encrypted)
///  2. Claude Code CLI OAuth token in ~/.claude/.credentials.json
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    public string Kind => "claude";
    public string Name => "Claude";
    public const string SessionKeySecret = "claude:sessionKey";
    const string OAuthClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"; // public Claude Code client

    static string CredPath => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(AppPaths.Home, ".claude"), ".credentials.json");

    public async Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        string? webError = null;
        if (SecretStore.Get(SessionKeySecret) is { Length: > 0 } sessionKey)
        {
            try { return new[] { await FromWeb(sessionKey, ct) }; }
            catch (Http.HttpError e) when (e.Code is 401) { webError = "claude.ai session expired. Sign in again from QuotaBar."; }
            catch (Http.HttpError e) when (e.Message.Contains("Just a moment")) { webError = "claude.ai blocked this request (Cloudflare). Open the Windows AI Quota Bar app; it fetches Claude through its browser view."; }
            catch (Http.HttpError e) { webError = "claude.ai: " + e.Message; }
            catch (Exception e) { webError = e.Message; }
        }
        try
        {
            if (await FromOAuth(ct) is { } s) return new[] { s };
        }
        catch (Exception e) { webError ??= e.Message; }

        return new[] { ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin,
            webError ?? "Sign in to claude.ai from Windows AI Quota Bar (Settings → Claude → Sign in), or run `claude` and /login.") };
    }

    // ---- claude.ai web session -------------------------------------------------------------

    static HttpRequestMessage WebReq(string url, string sessionKey)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("Cookie", "sessionKey=" + sessionKey);
        r.Headers.UserAgent.Clear();
        r.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        r.Headers.Add("anthropic-client-platform", "web_claude_ai");
        return r;
    }

    /// <summary>
    /// claude.ai sits behind Cloudflare, which rejects .NET's TLS fingerprint. The desktop app plugs in a
    /// fetcher that runs inside its (hidden) WebView2, i.e. a real browser. Returns (status, body).
    /// </summary>
    public static Func<string, CancellationToken, Task<(int status, string body)>>? WebFetcher { get; set; }

    static async Task<JsonElement> GetWeb(string url, string sessionKey, CancellationToken ct)
    {
        if (WebFetcher is { } f)
        {
            var (status, body) = await f(url, ct);
            if (status is < 200 or >= 300) throw new Http.HttpError(status, body);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        return await Http.SendJson(WebReq(url, sessionKey), ct);
    }

    async Task<ProviderSnapshot> FromWeb(string sessionKey, CancellationToken ct)
    {
        var orgs = await GetWeb("https://claude.ai/api/organizations", sessionKey, ct);
        JsonElement? org = null;
        int best = -1;
        foreach (var o in orgs.EnumerateArray())
        {
            var caps = o.Prop("capabilities")?.ToString() ?? "";
            var score = caps.Contains("claude_max") ? 3 : caps.Contains("claude_pro") ? 2 : caps.Contains("chat") ? 1 : 0;
            if (score > best) { best = score; org = o; }
        }
        if (org is null) throw new InvalidOperationException("No Claude organization found for this login.");
        var id = org.Value.Str("uuid")!;
        var usage = await GetWeb($"https://claude.ai/api/organizations/{id}/usage", sessionKey, ct);
        var s = Base();
        s.Plan = PlanName(org.Value.Str("rate_limit_tier"), org.Value.Prop("capabilities")?.ToString());
        var name = org.Value.Str("name") ?? "";
        s.Account = name.EndsWith("'s Organization") ? name[..^"'s Organization".Length] : name;
        ParseUsage(s, usage);
        return s;
    }

    // ---- Claude Code OAuth -------------------------------------------------------------------

    async Task<ProviderSnapshot?> FromOAuth(CancellationToken ct)
    {
        if (!File.Exists(CredPath)) return null;
        var text = await File.ReadAllTextAsync(CredPath, ct);
        var root = JsonNode.Parse(text)?.AsObject();
        var o = root?["claudeAiOauth"]?.AsObject();
        if (o is null) return null;
        var access = o["accessToken"]?.GetValue<string>();
        var refresh = o["refreshToken"]?.GetValue<string>();
        var expiresAt = o["expiresAt"]?.GetValue<long>() ?? 0;
        if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh)) return null;

        if ((string.IsNullOrEmpty(access) || expiresAt < DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()) && !string.IsNullOrEmpty(refresh))
        {
            // Refresh without trampling a refresh Claude Code may be doing at the same time (see TokenFile).
            var fresh = await TokenFile.RefreshAsync(CredPath, text, async current =>
            {
                var cur = JsonNode.Parse(current)!.AsObject();
                var co = cur["claudeAiOauth"]!.AsObject();
                var r = await Http.SendJson(new HttpRequestMessage(HttpMethod.Post, "https://console.anthropic.com/v1/oauth/token")
                {
                    Content = JsonContent.Create(new { grant_type = "refresh_token", refresh_token = co["refreshToken"]!.GetValue<string>(), client_id = OAuthClientId }),
                }, ct);
                co["accessToken"] = r.Str("access_token");
                if (r.Str("refresh_token") is { } nr) co["refreshToken"] = nr;
                co["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(r.Num("expires_in") ?? 3600).ToUnixTimeMilliseconds();
                return cur.ToJsonString();
            }, ct);
            access = JsonNode.Parse(fresh)?["claudeAiOauth"]?["accessToken"]?.GetValue<string>();
        }
        if (string.IsNullOrEmpty(access)) return null;

        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new("Bearer", access);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        var usage = await Http.SendJson(req, ct);
        var s = Base();
        s.Plan = PlanName(o["rateLimitTier"]?.GetValue<string>(), o["subscriptionType"]?.GetValue<string>());
        ParseUsage(s, usage);
        s.Info.Add(new("Source", "Claude Code login"));
        return s;
    }

    // ---- shared --------------------------------------------------------------------------------

    ProviderSnapshot Base() => new()
    {
        Id = Kind, ProviderKind = Kind, DisplayName = Name, Status = ProviderStatus.Ok,
        DashboardUrl = "https://claude.ai/settings/usage", StatusUrl = "https://status.claude.com",
    };

    static readonly (string key, string label, long secs)[] Known =
    {
        ("five_hour", "Session", 18000),
        ("seven_day", "Weekly", 604800),
        ("seven_day_opus", "Weekly · Opus", 604800),
        ("seven_day_sonnet", "Weekly · Sonnet", 604800),
        ("seven_day_oauth_apps", "Weekly · Apps", 604800),
    };

    static void ParseUsage(ProviderSnapshot s, JsonElement u)
    {
        foreach (var (key, label, secs) in Known)
        {
            if (u.Prop(key) is not { ValueKind: JsonValueKind.Object } w || w.Num("utilization") is not { } util) continue;
            s.Windows.Add(new UsageWindow { Key = key, Label = label, UsedPercent = util, ResetsAt = w.Date("resets_at"), WindowSeconds = secs });
        }
        // Any new window types Anthropic adds later.
        foreach (var p in u.EnumerateObject())
        {
            if (Known.Any(k => k.key == p.Name) || p.Value.ValueKind != JsonValueKind.Object || p.Value.Num("utilization") is not { } util) continue;
            if (p.Name == "extra_usage") continue;
            s.Windows.Add(new UsageWindow
            {
                Key = p.Name, Label = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.Name.Replace('_', ' ')),
                UsedPercent = util, ResetsAt = p.Value.Date("resets_at"),
                WindowSeconds = p.Name.StartsWith("seven_day") ? 604800 : p.Name.StartsWith("five_hour") ? 18000 : null,
            });
        }
        if (u.Prop("extra_usage") is { ValueKind: JsonValueKind.Object } ex && ex.Prop("is_enabled")?.ValueKind == JsonValueKind.True)
        {
            var used = (ex.Num("used_credits") ?? 0) / 100.0;
            var limit = (ex.Num("monthly_limit") ?? 0) / 100.0;
            s.Info.Add(new("Extra usage", limit > 0 ? $"${used:0.00} of ${limit:0}" : $"${used:0.00}"));
        }
    }

    static string? PlanName(string? tier, string? fallback)
    {
        var t = (tier ?? "") + " " + (fallback ?? "");
        if (t.Contains("max_20x")) return "Max 20x";
        if (t.Contains("max_5x")) return "Max 5x";
        if (t.Contains("max")) return "Max";
        if (t.Contains("pro")) return "Pro";
        if (t.Contains("team")) return "Team";
        if (t.Contains("enterprise")) return "Enterprise";
        return string.IsNullOrWhiteSpace(t) ? null : "Free";
    }
}
