namespace QuotaBar.Core.Providers;

/// <summary>Presents SwitcherCore snapshots; never reads or refreshes Codex credentials itself.</summary>
public sealed class CodexProvider(Func<IReadOnlyList<ProviderSnapshot>>? source = null) : IUsageProvider
{
    public string Kind => "codex";
    public string Name => "Codex";
    public Task<IReadOnlyList<ProviderSnapshot>> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The desktop supplies live shared-core state. The CLI reads the desktop's last snapshot.
        var snapshots = source?.Invoke()
            ?? UsageService.LoadCached()?.Providers.Where(p => p.ProviderKind == Kind).ToArray()
            ?? Array.Empty<ProviderSnapshot>();
        return Task.FromResult<IReadOnlyList<ProviderSnapshot>>(snapshots.Count > 0 ? snapshots : new[]
        {
            ProviderSnapshot.Fail(Kind, Kind, Name, ProviderStatus.NeedsLogin,
                "Open Quota Bar → Codex accounts to connect your accounts."),
        });
    }
    internal static string WindowName(long? seconds, string fallback) => seconds switch
    {
        18000 => "Session", 604800 => "Weekly", 86400 => "Daily",
        { } x when x % 3600 == 0 && x < 86400 => $"{x / 3600}-hour", _ => fallback,
    };
}
