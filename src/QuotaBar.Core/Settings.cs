namespace QuotaBar.Core;

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowTaskbarWidget { get; set; } = true;
    /// <summary>Taskbar widget look: "rings" or "bars".</summary>
    public string WidgetStyle { get; set; } = "rings";
    /// <summary>Metrics shown in the taskbar widget, as "providerId|windowKey". Max 2.</summary>
    public List<string> WidgetMetrics { get; set; } = new() { "claude|five_hour", "codex|primary" };
    /// <summary>Horizontal offset (px) of the widget from the tray area; user can drag it.</summary>
    public int WidgetOffset { get; set; } = 0;
    public Dictionary<string, bool> EnabledProviders { get; set; } = new();
    /// <summary>Extra OpenCode Go API keys (besides the ones read from OpenCode itself). Stored DPAPI-encrypted, here only labels.</summary>
    public List<string> OpenCodeExtraKeyLabels { get; set; } = new();
    public string? LastSelectedProvider { get; set; }
    /// <summary>Accounts added with an API key / token (keys are stored encrypted, not here).</summary>
    public List<Providers.KeyAccount> KeyAccounts { get; set; } = new();

    /// <summary>Demo mode: never written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; set; }

    public bool IsEnabled(string kind) => !EnabledProviders.TryGetValue(kind, out var on) || on;

    public static AppSettings Load() => Json.Read<AppSettings>(AppPaths.Settings) ?? new AppSettings();
    public void Save() { if (!ReadOnly) Json.WriteAtomic(AppPaths.Settings, this); }
}
