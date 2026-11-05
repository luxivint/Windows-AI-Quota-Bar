using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuotaBar.Core.Providers;

/// <summary>
/// Google Antigravity (agy CLI / IDE). The signed-in account lives in Windows Credential Manager
/// under "gemini:antigravity". QuotaBar keeps a DPAPI-encrypted copy of every account it has seen,
/// so it can show all of them and swap the active one.
/// </summary>
public static class AntigravityAccounts
{
    public const string CredTarget = "gemini:antigravity";
    const string IndexKey = "antigravity:index";
    static string VaultKey(string email) => "antigravity:account:" + email.ToLowerInvariant();

    public sealed record Account(string Email, bool Active);

    public static string? ActiveEmail()
    {
        var c = WinCred.Read(CredTarget);
        if (c is null) return null;
        try { return JsonNode.Parse(c.Value.blob)?["id_token_email"]?.GetValue<string>(); } catch { return null; }
    }

    /// <summary>Copies the currently signed-in agy account into the vault (always the freshest token).</summary>
    public static string? SyncActive()
    {
        var c = WinCred.Read(CredTarget);
        if (c is null) return null;
        var json = Encoding.UTF8.GetString(c.Value.blob);
        string? email;
        try { email = JsonNode.Parse(json)?["id_token_email"]?.GetValue<string>(); } catch { return null; }
        if (string.IsNullOrEmpty(email)) return null;
        SecretStore.Set(VaultKey(email), json);
        var idx = Index();
        if (!idx.Contains(email, StringComparer.OrdinalIgnoreCase)) { idx.Add(email); SaveIndex(idx); }
        return email;
    }

    public static List<Account> List()
    {
        var active = SyncActive() ?? ActiveEmail();
        return Index().Select(e => new Account(e, string.Equals(e, active, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static void Switch(string email)
    {
        SyncActive();
        var match = Index().FirstOrDefault(e => e.Equals(email, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown Antigravity account: {email}");
        var json = SecretStore.Get(VaultKey(match)) ?? throw new InvalidOperationException("Stored token missing; sign in again.");
        WinCred.Write(CredTarget, "antigravity", Encoding.UTF8.GetBytes(json));
    }

    public static void Remove(string email)
    {
        SecretStore.Set(VaultKey(email), null);
        SaveIndex(Index().Where(e => !e.Equals(email, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>Signs the agy CLI out (after saving the current account) so the next `agy` run asks for a new login.</summary>
    public static string? PrepareAddAccount()
    {
        var prev = SyncActive();
        WinCred.Delete(CredTarget);
        return prev;
    }

    public static string? AgyPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var p = Path.Combine(local, "agy", "bin", "agy.exe");
        return File.Exists(p) ? p : null;
    }

    static List<string> Index()
    {
        try { return JsonSerializer.Deserialize<List<string>>(SecretStore.Get(IndexKey) ?? "[]") ?? new(); } catch { return new(); }
    }
    static void SaveIndex(List<string> idx) => SecretStore.Set(IndexKey, JsonSerializer.Serialize(idx));

    internal static string? VaultJson(string email) => SecretStore.Get(VaultKey(email));
    internal static void SaveVaultJson(string email, string json) => SecretStore.Set(VaultKey(email), json);

    // ---- token refresh -------------------------------------------------------------------

    /// <summary>
    /// Returns a valid access token for a vaulted account, refreshing it if needed. The OAuth client
    /// is read at runtime from the installed Antigravity binary, so no secret ships with QuotaBar.
    /// </summary>
    internal static async Task<string> AccessToken(string email, CancellationToken ct)
    {
        var json = VaultJson(email) ?? throw new InvalidOperationException("Account not stored.");
        var node = JsonNode.Parse(json)!.AsObject();
        var tok = node["token"]!.AsObject();
        var access = tok["access_token"]?.GetValue<string>();
        var expiry = tok["expiry"]?.GetValue<string>() is { } ex && DateTimeOffset.TryParse(ex, out var d) ? d : DateTimeOffset.MinValue;
        if (!string.IsNullOrEmpty(access) && expiry > DateTimeOffset.Now.AddMinutes(2)) return access;

        var rt = tok["refresh_token"]?.GetValue<string>() ?? throw new InvalidOperationException("No refresh token; sign in again.");
        var res = await RefreshWithDiscoveredClient(rt, ct);
        access = res.Str("access_token")!;
        tok["access_token"] = access;
        var secs = res.Num("expires_in") ?? 3600;
        tok["expiry"] = DateTimeOffset.Now.AddSeconds(secs).ToString("O");
        tok["expires_in"] = (int)secs;
        SaveVaultJson(email, node.ToJsonString());
        return access;
    }

    static async Task<JsonElement> RefreshWithDiscoveredClient(string refreshToken, CancellationToken ct)
    {
        var candidates = new List<(string id, string secret)>();
        if (SecretStore.Get("antigravity:oauthclient") is { } cached && cached.Split('\n') is [var cid, var csec])
            candidates.Add((cid, csec));
        candidates.AddRange(DiscoverClients().Where(c => !candidates.Contains(c)));
        if (candidates.Count == 0) throw new InvalidOperationException("Antigravity (agy) is not installed, so the token cannot be refreshed. Run `agy` once.");

        foreach (var (id, secret) in candidates)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = id, ["client_secret"] = secret, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token",
                }),
            };
            try
            {
                var r = await Http.SendJson(req, ct);
                SecretStore.Set("antigravity:oauthclient", id + "\n" + secret);
                return r;
            }
            catch (Http.HttpError e) when (e.Code is 400 or 401) { /* wrong pair or revoked; try next */ }
        }
        throw new InvalidOperationException("Antigravity sign-in expired. Switch to this account and run `agy` to sign in again.");
    }

    static IEnumerable<(string, string)> DiscoverClients()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var files = new List<string>();
        if (AgyPath() is { } agy) files.Add(agy);
        var ide = Path.Combine(local, "Programs", "Antigravity");
        if (Directory.Exists(ide))
            files.AddRange(Directory.EnumerateFiles(ide, "language_server*.exe", SearchOption.AllDirectories));

        var ids = new List<string>(); var secrets = new List<string>();
        foreach (var f in files)
        {
            try
            {
                var text = Encoding.Latin1.GetString(File.ReadAllBytes(f));
                ids.AddRange(Regex.Matches(text, @"\d{6,}-[a-z0-9]{20,}\.apps\.googleusercontent\.com").Select(m => m.Value));
                secrets.AddRange(Regex.Matches(text, @"GOCSPX-[A-Za-z0-9_\-]{28}").Select(m => m.Value));
            }
            catch { }
        }
        foreach (var id in ids.Distinct())
            foreach (var s in secrets.Distinct())
                yield return (id, s);
    }
}

public sealed class AntigravityProvider : IUsageProvider
{
    public string Kind => "antigravity";
    public string Name => "Antigravity";
    const string Api = "https://cloudcode-pa.googleapis.com/v1internal:";

    public async Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        var accounts = AntigravityAccounts.List();
        if (accounts.Count == 0)
            return new[] { ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin, "Sign in with `agy` (Antigravity CLI) once, then refresh.") };
        return await Task.WhenAll(accounts.Select(a => FetchOne(a, ct)));
    }

    async Task<ProviderSnapshot> FetchOne(AntigravityAccounts.Account a, CancellationToken ct)
    {
        var s = new ProviderSnapshot
        {
            Id = $"{Kind}:{a.Email}", ProviderKind = Kind, DisplayName = Name, Account = a.Email, IsActiveAccount = a.Active,
            DashboardUrl = "https://antigravity.google", StatusUrl = "https://www.google.com/appsstatus/dashboard/",
        };
        try
        {
            var token = await AntigravityAccounts.AccessToken(a.Email, ct);
            var load = await Post(token, "loadCodeAssist", new JsonObject { ["metadata"] = new JsonObject { ["ideType"] = "ANTIGRAVITY" } }, ct);
            s.Plan = load.Prop("paidTier")?.Str("name") ?? load.Prop("currentTier")?.Str("name");
            var project = load.Str("cloudaicompanionProject");
            var body = new JsonObject();
            if (project is not null) body["project"] = project;
            var models = await Post(token, "fetchAvailableModels", body, ct);
            ParseModels(s, models);
            s.Status = ProviderStatus.Ok;
        }
        catch (Exception e)
        {
            s.Status = e is InvalidOperationException ? ProviderStatus.NeedsLogin : ProviderStatus.Error;
            s.Message = e.Message;
        }
        return s;
    }

    static async Task<JsonElement> Post(string token, string method, JsonObject body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + method) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.UserAgent.ParseAdd("antigravity");
        return await Http.SendJson(req, ct);
    }

    static readonly (string key, string label, Func<string, bool> match)[] Families =
    {
        ("gemini-pro", "Gemini Pro", id => id.StartsWith("gemini") && id.Contains("pro")),
        ("gemini-flash", "Gemini Flash", id => id.StartsWith("gemini") && id.Contains("flash") && !id.Contains("image")),
        ("claude", "Claude", id => id.StartsWith("claude")),
        ("gpt", "GPT-OSS", id => id.StartsWith("gpt")),
        ("image", "Image", id => id.Contains("image")),
    };

    static void ParseModels(ProviderSnapshot s, JsonElement json)
    {
        if (json.Prop("models") is not { ValueKind: JsonValueKind.Object } models) return;
        var buckets = new Dictionary<string, (double used, DateTimeOffset? reset, List<string> names)>();
        foreach (var m in models.EnumerateObject())
        {
            var id = m.Name.ToLowerInvariant();
            var display = m.Value.Str("displayName");
            if (display is null || m.Value.Prop("quotaInfo") is not { } q) continue;
            var fam = Families.FirstOrDefault(f => f.match(id));
            if (fam.key is null) continue;
            var used = (1 - (q.Num("remainingFraction") ?? 1)) * 100;
            var reset = q.Date("resetTime");
            buckets.TryGetValue(fam.key, out var b);
            b.names ??= new();
            if (!b.names.Contains(display)) b.names.Add(display);
            b.used = Math.Max(b.used, used);
            if (reset is not null && (b.reset is null || reset < b.reset)) b.reset = reset;
            buckets[fam.key] = b;
        }
        foreach (var f in Families)
        {
            if (!buckets.TryGetValue(f.key, out var b)) continue;
            s.Windows.Add(new UsageWindow
            {
                Key = f.key, Label = f.label, UsedPercent = Math.Round(b.used, 1), ResetsAt = b.reset,
                Detail = string.Join(", ", b.names.Take(4)) + (b.names.Count > 4 ? $" +{b.names.Count - 4}" : ""),
            });
        }
    }
}
