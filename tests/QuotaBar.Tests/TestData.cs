namespace QuotaBar.Tests;

/// <summary>Points AppPaths.Data at a fresh temp folder so tests never touch the real %LOCALAPPDATA%\QuotaBar.</summary>
public sealed class TempData : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "qb-tests-" + Guid.NewGuid().ToString("N"));
    public TempData()
    {
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("QUOTABAR_DATA", Dir);
        History.ResetCache();
    }
    public void Dispose()
    {
        History.ResetCache();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

[CollectionDefinition("data", DisableParallelization = true)]
public sealed class DataCollection { }

static class Make
{
    public static ProviderSnapshot Provider(string id, DateTimeOffset fetched, params UsageWindow[] windows) => new()
    {
        Id = id, ProviderKind = id.Split(':')[0], DisplayName = id, Status = ProviderStatus.Ok, FetchedAt = fetched,
        Windows = windows.ToList(),
    };

    public static UsageWindow Window(string key, double used, DateTimeOffset? resetsAt, long? len = null) =>
        new() { Key = key, Label = key, UsedPercent = used, ResetsAt = resetsAt, WindowSeconds = len };

    public static UsageReport Report(params ProviderSnapshot[] p) => new() { Providers = p.ToList() };
}
