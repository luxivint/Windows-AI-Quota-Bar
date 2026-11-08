using QuotaBar.Core.Providers;

namespace QuotaBar.Core;

public interface IUsageProvider
{
    /// <summary>"claude", "codex", "antigravity", "opencode-go".</summary>
    string Kind { get; }
    string Name { get; }
    /// <summary>A provider may return several snapshots (one per account).</summary>
    Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct);
}

public sealed class UsageService
{
    public IReadOnlyList<IUsageProvider> Providers { get; }

    public UsageService(IUsageProvider? codex = null) => Providers = new IUsageProvider[]
    {
        new ClaudeProvider(),
        codex ?? new CodexProvider(),
        new AntigravityProvider(),
        new OpenCodeGoProvider(),
    }.Concat(KeyProviders.Catalog.Select(d => (IUsageProvider)new KeyAccountsProvider(d))).ToList();

    /// <summary>Providers that are always shown (they discover their login automatically).</summary>
    public static readonly string[] BuiltIn = { "claude", "codex", "antigravity", "opencode-go" };

    public async Task<UsageReport> RefreshAsync(AppSettings settings, string? onlyKind = null, CancellationToken ct = default)
    {
        var selected = Providers.Where(p => onlyKind is null || p.Kind.Equals(onlyKind, StringComparison.OrdinalIgnoreCase)).ToList();
        var tasks = selected.Select(async p =>
        {
            if (p is KeyAccountsProvider && !settings.KeyAccounts.Any(a => a.Kind == p.Kind))
                return (IReadOnlyList<ProviderSnapshot>)Array.Empty<ProviderSnapshot>();
            if (!settings.IsEnabled(p.Kind))
                return (IReadOnlyList<ProviderSnapshot>)new[] { ProviderSnapshot.Fail(p.Kind, p.Kind, p.Name, ProviderStatus.Disabled, "Disabled in settings") };
            try
            {
                var r = await p.FetchAsync(ct);
                // One retry for transient network/TLS failures (common right after wake from sleep).
                if (r.Any(x => x.Status == ProviderStatus.Error && (x.Message?.Contains("SSL") == true || x.Message?.Contains("connection") == true)))
                {
                    await Task.Delay(1500, ct);
                    r = await p.FetchAsync(ct);
                }
                return r;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(1500, ct);
                try { return await p.FetchAsync(ct); }
                catch (Exception ex) { return new[] { ProviderSnapshot.Fail(p.Kind, p.Kind, p.Name, ProviderStatus.Error, ex.GetBaseException().Message) }; }
            }
            catch (Exception ex) { return new[] { ProviderSnapshot.Fail(p.Kind, p.Kind, p.Name, ProviderStatus.Error, ex.Message) }; }
        });
        var results = await Task.WhenAll(tasks);
        var report = new UsageReport { Providers = results.SelectMany(r => r).ToList() };

        // If a live fetch failed but the app fetched it recently (e.g. Claude via the app's browser view), keep that.
        var prev = LoadCached();
        if (prev != null)
        {
            for (int i = 0; i < report.Providers.Count; i++)
            {
                var cur = report.Providers[i];
                if (cur.Status is ProviderStatus.Ok or ProviderStatus.Disabled) continue;
                // A failed Codex fetch cannot establish which account is active. Do not show another account's cached quota.
                if (cur.ProviderKind == "codex") continue;
                var old = prev.Providers.FirstOrDefault(p => p.Id == cur.Id && p.Status == ProviderStatus.Ok);
                if (old != null && DateTimeOffset.Now - old.FetchedAt < TimeSpan.FromMinutes(30))
                {
                    old.Message = null;
                    if (!old.Info.Any(x => x.Label == "Cached")) old.Info.Add(new("Cached", "from " + old.FetchedAt.ToLocalTime().ToString("HH:mm")));
                    report.Providers[i] = old;
                }
            }
        }

        // Merge into the cached snapshot so a partial refresh keeps the others.
        if (onlyKind is not null && LoadCached() is { } cached)
        {
            var merged = cached.Providers.Where(p => !p.ProviderKind.Equals(onlyKind, StringComparison.OrdinalIgnoreCase)).ToList();
            merged.AddRange(report.Providers);
            report.Providers = Order(merged);
        }
        else report.Providers = Order(report.Providers);

        try { Json.WriteAtomic(AppPaths.Snapshot, report); } catch { /* another process may be writing */ }
        return report;
    }

    static List<ProviderSnapshot> Order(IEnumerable<ProviderSnapshot> s)
    {
        var order = BuiltIn.Concat(KeyProviders.Catalog.Select(d => d.Kind)).ToArray();
        return s.OrderBy(p => Array.IndexOf(order, p.ProviderKind) is var i && i < 0 ? 99 : i)
                .ThenByDescending(p => p.IsActiveAccount).ThenBy(p => p.Account).ToList();
    }

    public static UsageReport? LoadCached() => Json.Read<UsageReport>(AppPaths.Snapshot);
}
