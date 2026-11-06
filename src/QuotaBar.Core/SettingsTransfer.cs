using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuotaBar.Core;

/// <summary>
/// Settings backup without secrets: API keys and their labels, window position and other per-PC state
/// stay out of the file, and importing keeps the keys already on this PC.
/// </summary>
public static class SettingsTransfer
{
    public const string AppName = "Windows AI Quota Bar";

    /// <summary>Properties (camelCase, as written by Json.Options) that never leave or enter this PC through a backup.</summary>
    static readonly string[] Local = ["keyAccounts", "openCodeExtraKeyLabels", "lastSelectedProvider", "widgetOffset", "notifiedUpdate"];

    public static string Export(AppSettings s, DateTimeOffset? now = null)
    {
        var node = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        foreach (var k in Local) node.Remove(k);
        var root = new JsonObject
        {
            ["app"] = AppName,
            ["format"] = 1,
            ["exported"] = (now ?? DateTimeOffset.Now).ToString("O"),
            ["settings"] = node,
        };
        return root.ToJsonString(Json.Options);
    }

    /// <summary>Settings from a backup, merged with the local-only parts of <paramref name="current"/>. Throws FormatException on a file that is not a backup.</summary>
    public static AppSettings Import(string json, AppSettings current)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { throw new FormatException("Not a JSON file."); }
        if (root is not JsonObject obj) throw new FormatException("Not a settings backup.");
        var settings = obj["settings"] as JsonObject ?? (obj.ContainsKey("refreshMinutes") ? obj : null);
        if (settings is null || (obj["app"] is { } app && app.GetValue<string>() != AppName)) throw new FormatException("Not a Windows AI Quota Bar settings backup.");
        foreach (var k in Local) settings.Remove(k);

        var s = settings.Deserialize<AppSettings>(Json.Options) ?? throw new FormatException("Empty settings.");
        s.KeyAccounts = current.KeyAccounts;
        s.OpenCodeExtraKeyLabels = current.OpenCodeExtraKeyLabels;
        s.LastSelectedProvider = current.LastSelectedProvider;
        s.WidgetOffset = current.WidgetOffset;
        s.NotifiedUpdate = current.NotifiedUpdate;
        s.RefreshMinutes = Math.Clamp(s.RefreshMinutes, 1, 120);
        s.Alerts ??= new();
        s.Alerts.Thresholds = (s.Alerts.Thresholds ?? []).Select(t => Math.Clamp(t, 1, 100)).Distinct().OrderBy(t => t).ToList();
        s.WidgetMetrics ??= new();
        s.EnabledProviders ??= new();
        return s;
    }
}
