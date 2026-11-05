using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaBar.Core.Providers;

/// <summary>OpenCode Go: reads API keys stored by OpenCode and asks /zen/go/v1/usage.</summary>
public sealed class OpenCodeGoProvider : IUsageProvider
{
    public string Kind => "opencode-go";
    public string Name => "OpenCode Go";

    public sealed record GoKey(string Id, string Label, string Key, bool Active);

    public async Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        var keys = DiscoverKeys();
        if (keys.Count == 0)
            return new[] { ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin, "No OpenCode Go key found. Add one in OpenCode (`/connect`) or in Windows AI Quota Bar settings.") };

        var tasks = keys.Select(async k =>
        {
            var id = $"{Kind}:{k.Id}";
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://opencode.ai/zen/go/v1/usage");
                req.Headers.Authorization = new("Bearer", k.Key);
                var json = await Http.SendJson(req, ct);
                return Parse(id, k, json);
            }
            catch (Http.HttpError e) when (e.Code is 401 or 403)
            {
                return WithAccount(ProviderSnapshot.Fail(id, Kind, Name, ProviderStatus.NeedsLogin, "API key rejected."), k);
            }
            catch (Exception e)
            {
                return WithAccount(ProviderSnapshot.Fail(id, Kind, Name, ProviderStatus.Error, e.Message), k);
            }
        });
        var results = (await Task.WhenAll(tasks)).ToList();

        // Several keys of the same Go subscription report identical usage: keep one (prefer the active key).
        static string Sig(ProviderSnapshot s) => string.Join(";", s.Windows.Where(w => w.Key != "rolling")
            .Select(w => $"{w.Key}:{w.UsedPercent:0.#}:{w.ResetsAt?.ToUnixTimeSeconds() / 60}"));
        return results
            .GroupBy(s => s.Status == ProviderStatus.Ok ? Sig(s) : s.Id)
            .Select(g => g.OrderByDescending(s => s.IsActiveAccount).First())
            .ToList();
    }

    static ProviderSnapshot WithAccount(ProviderSnapshot s, GoKey k) { s.Account = k.Label; s.IsActiveAccount = k.Active; return s; }

    ProviderSnapshot Parse(string id, GoKey k, JsonElement json)
    {
        var s = new ProviderSnapshot
        {
            Id = id, ProviderKind = Kind, DisplayName = Name, Account = k.Label, IsActiveAccount = k.Active,
            Plan = "Go", Status = ProviderStatus.Ok,
            DashboardUrl = "https://opencode.ai/auth",
            StatusUrl = "https://status.opencode.ai",
        };
        var u = json.Prop("usage") ?? json;
        foreach (var (key, label, secs) in new (string, string, long?)[] { ("rolling", "Session", 18000), ("weekly", "Weekly", 604800), ("monthly", "Monthly", null) })
        {
            if (u.Prop(key) is not { } w) continue;
            var reset = w.Date("resetsAt");
            long? len = secs ?? (reset is { } r ? (long)(r - r.AddMonths(-1)).TotalSeconds : null);
            s.Windows.Add(new UsageWindow { Key = key, Label = label, UsedPercent = w.Num("percent") ?? 0, ResetsAt = reset, WindowSeconds = len });
            if (w.Str("status") is { } st && st != "ok") s.Message = $"{label}: {st}";
        }
        return s;
    }

    public static List<GoKey> DiscoverKeys()
    {
        var list = new List<GoKey>();
        var db = Path.Combine(AppPaths.Home, ".local", "share", "opencode", "opencode.db");
        if (File.Exists(db))
        {
            try
            {
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString());
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "select id, label, value, active from credential where integration_id = 'opencode-go'";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    using var v = JsonDocument.Parse(r.GetString(2));
                    if (v.RootElement.Str("key") is { Length: > 0 } key)
                        list.Add(new GoKey(r.GetString(0), r.IsDBNull(1) ? "OpenCode Go" : r.GetString(1), key, !r.IsDBNull(3) && r.GetInt64(3) == 1));
                }
            }
            catch { /* schema changed or db locked: fall through to other sources */ }
        }
        // Legacy auth.json used by older OpenCode versions.
        var authJson = Path.Combine(AppPaths.Home, ".local", "share", "opencode", "auth.json");
        if (File.Exists(authJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authJson));
                if (doc.RootElement.Prop("opencode-go") is { } e && e.Str("key") is { Length: > 0 } key && list.All(x => x.Key != key))
                    list.Add(new GoKey("auth-json", "OpenCode Go", key, list.Count == 0));
            }
            catch { }
        }
        var settings = AppSettings.Load();
        foreach (var label in settings.OpenCodeExtraKeyLabels)
            if (SecretStore.Get("opencode-go:" + label) is { Length: > 0 } key && list.All(x => x.Key != key))
                list.Add(new GoKey("extra-" + label, label, key, false));
        return list;
    }
}

