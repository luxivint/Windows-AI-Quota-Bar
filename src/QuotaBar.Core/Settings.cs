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
    /// <summary>Show the limits closest to running out instead of <see cref="WidgetMetrics"/>.</summary>
    public bool WidgetAuto { get; set; } = false;
    /// <summary>Also show the widget on the taskbars of other monitors.</summary>
    public bool WidgetAllMonitors { get; set; } = false;
    /// <summary>Global shortcut that opens the panel, e.g. "Ctrl+Alt+Q". Empty = off.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Q";
    public AlertSettings Alerts { get; set; } = new();
    /// <summary>Poll the providers' public status pages for incidents.</summary>
    public bool CheckServiceStatus { get; set; } = true;
    /// <summary>Price local Claude Code logs at API rates (token counts only, nothing leaves the PC).</summary>
    public bool ScanLocalCost { get; set; } = true;
    public Dictionary<string, bool> EnabledProviders { get; set; } = new();
    /// <summary>Extra OpenCode Go API keys (besides the ones read from OpenCode itself). Stored DPAPI-encrypted, here only labels.</summary>
    public List<string> OpenCodeExtraKeyLabels { get; set; } = new();
    public string? LastSelectedProvider { get; set; }
    /// <summary>Accounts added with an API key / token (keys are stored encrypted, not here).</summary>
    public List<Providers.KeyAccount> KeyAccounts { get; set; } = new();
    /// <summary>Look for a new release on GitHub (at start and every 6 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Expose the switch_antigravity_account MCP tool. Off by default: AI assistants can only read usage.</summary>
    public bool AllowAssistantAccountSwitch { get; set; } = false;
    /// <summary>Install new releases without asking (only for installed copies).</summary>
    public bool AutoInstallUpdates { get; set; } = false;
    /// <summary>Last version we showed a notification for, so each release notifies once.</summary>
    public string? NotifiedUpdate { get; set; }

    /// <summary>Demo mode: never written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool ReadOnly { get; set; }

    public bool IsEnabled(string kind) => !EnabledProviders.TryGetValue(kind, out var on) || on;

    public static AppSettings Load() => Json.Read<AppSettings>(AppPaths.Settings) ?? new AppSettings();
    public void Save() { if (!ReadOnly) { Json.WriteAtomic(AppPaths.Settings, this); LastSaved = DateTime.UtcNow; } }

    /// <summary>When this process last wrote settings.json (to tell our own writes from `qb config`).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public static DateTime LastSaved { get; private set; }
}
