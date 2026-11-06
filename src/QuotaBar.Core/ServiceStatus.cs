using System.Text.Json;

namespace QuotaBar.Core;

public enum Health { Unknown, Operational, Minor, Major, Critical }

/// <summary>Current service health of one provider, from its public status page.</summary>
public sealed record ServiceHealth(string Kind, Health Health, string Description, string PageUrl, DateTimeOffset CheckedAt);

/// <summary>
/// Polls the providers' public status pages (no credentials involved):
/// Statuspage.io `api/v2/status.json` for Claude, OpenAI and GitHub, and the Google Workspace
/// incidents feed (Gemini product) for Antigravity.
/// </summary>
public static class ServiceStatus
{
    sealed record Source(string Url, string Page, string? GoogleProduct = null);

    static readonly Dictionary<string, Source> Sources = new()
    {
        ["claude"] = new("https://status.claude.com/api/v2/status.json", "https://status.claude.com"),
        ["codex"] = new("https://status.openai.com/api/v2/status.json", "https://status.openai.com"),
        ["copilot"] = new("https://www.githubstatus.com/api/v2/status.json", "https://www.githubstatus.com"),
        ["antigravity"] = new("https://www.google.com/appsstatus/dashboard/incidents.json", "https://www.google.com/appsstatus/dashboard/", "npdyhgECDJ6tB66MxXyo"),
    };

    public static bool Supports(string kind) => Sources.ContainsKey(kind);

    public static async Task<List<ServiceHealth>> CheckAsync(IEnumerable<string> kinds, CancellationToken ct)
    {
        var tasks = kinds.Distinct().Where(Sources.ContainsKey).Select(async k =>
        {
            var src = Sources[k];
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var resp = await Http.Client.SendAsync(req, cts.Token);
                resp.EnsureSuccessStatusCode();
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                return src.GoogleProduct is { } product ? ParseGoogle(k, text, product, src.Page) : ParseStatuspage(k, text, src.Page);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return new ServiceHealth(k, Health.Unknown, "", src.Page, DateTimeOffset.Now);
            }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    public static ServiceHealth ParseStatuspage(string kind, string json, string page)
    {
        using var doc = JsonDocument.Parse(json);
        var st = doc.RootElement.GetProperty("status");
        var health = st.Str("indicator") switch
        {
            "none" => Health.Operational,
            "minor" or "maintenance" => Health.Minor,
            "major" => Health.Major,
            "critical" => Health.Critical,
            _ => Health.Unknown,
        };
        return new ServiceHealth(kind, health, st.Str("description") ?? "", page, DateTimeOffset.Now);
    }

    /// <summary>Most severe ongoing incident (no "end") that affects the given product.</summary>
    public static ServiceHealth ParseGoogle(string kind, string json, string productId, string page)
    {
        using var doc = JsonDocument.Parse(json);
        var worst = Health.Operational;
        var desc = "";
        foreach (var inc in doc.RootElement.EnumerateArray())
        {
            if (inc.Prop("end") is not null) continue;
            if (inc.Prop("affected_products") is not { ValueKind: JsonValueKind.Array } prods
                || !prods.EnumerateArray().Any(p => p.Str("id") == productId)) continue;
            var h = inc.Str("status_impact") switch
            {
                "SERVICE_OUTAGE" => Health.Critical,
                "SERVICE_DISRUPTION" => inc.Str("severity") == "high" ? Health.Major : Health.Minor,
                _ => Health.Minor,
            };
            if (h <= worst) continue;
            worst = h;
            // "**Summary:**\nMitigating elevated error rate…" → first sentence after the summary heading.
            var text = (inc.Str("external_desc") ?? "").Replace("**Summary:**", "").Trim();
            desc = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        }
        return new ServiceHealth(kind, worst, worst == Health.Operational ? "All Systems Operational" : desc, page, DateTimeOffset.Now);
    }
}
