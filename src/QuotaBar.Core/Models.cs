using System.Text.Json.Serialization;

namespace QuotaBar.Core;

public enum ProviderStatus { Ok, NeedsLogin, Error, Disabled }

/// <summary>One rate-limit window, e.g. "5-hour session" or "weekly".</summary>
public sealed class UsageWindow
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>0-100, how much of the window is used.</summary>
    public double UsedPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    /// <summary>Total length of the window in seconds, when known. Enables pace calculation.</summary>
    public long? WindowSeconds { get; set; }
    /// <summary>Optional free-form detail shown under the bar ("$3.20 of $15").</summary>
    public string? Detail { get; set; }

    [JsonIgnore] public double LeftPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    /// <summary>
    /// Pace: positive = using faster than an even burn would allow (in percentage points).
    /// Null when the window length or reset time is unknown.
    /// </summary>
    public double? PaceDelta(DateTimeOffset now)
    {
        if (WindowSeconds is not { } len || len <= 0 || ResetsAt is not { } reset) return null;
        var remaining = (reset - now).TotalSeconds;
        var elapsed = Math.Clamp(len - remaining, 0, len);
        var expected = elapsed / len * 100.0;
        return UsedPercent - expected;
    }

    /// <summary>Projected time the window runs out at the current burn rate (null if it will not).</summary>
    public DateTimeOffset? ProjectedRunOut(DateTimeOffset now)
    {
        if (WindowSeconds is not { } len || len <= 0 || ResetsAt is not { } reset) return null;
        var elapsed = len - (reset - now).TotalSeconds;
        if (elapsed <= 60 || UsedPercent <= 0) return null;
        var ratePerSec = UsedPercent / elapsed;
        var secsToFull = (100 - UsedPercent) / ratePerSec;
        var runOut = now.AddSeconds(secsToFull);
        return runOut < reset ? runOut : null;
    }
}

public sealed class InfoLine
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public InfoLine() { }
    public InfoLine(string label, string value) { Label = label; Value = value; }
}

/// <summary>Everything we know about one provider account at one point in time.</summary>
public sealed class ProviderSnapshot
{
    /// <summary>Stable id, e.g. "claude", "codex", "antigravity:me@gmail.com", "opencode-go:cred_x".</summary>
    public string Id { get; set; } = "";
    public string ProviderKind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Account { get; set; }
    public string? Plan { get; set; }
    public ProviderStatus Status { get; set; }
    public string? Message { get; set; }
    public bool IsActiveAccount { get; set; } = true;
    public List<UsageWindow> Windows { get; set; } = new();
    public List<InfoLine> Info { get; set; } = new();
    public string? DashboardUrl { get; set; }
    public string? StatusUrl { get; set; }
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.Now;

    public static ProviderSnapshot Fail(string id, string kind, string name, ProviderStatus status, string message) =>
        new() { Id = id, ProviderKind = kind, DisplayName = name, Status = status, Message = message };
}

public sealed class UsageReport
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public List<ProviderSnapshot> Providers { get; set; } = new();
}
