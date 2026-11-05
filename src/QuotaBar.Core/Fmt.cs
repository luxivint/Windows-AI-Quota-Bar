namespace QuotaBar.Core;

public static class Fmt
{
    /// <summary>"4h 55m", "6d 3h", "38s".</summary>
    public static string Until(DateTimeOffset t, DateTimeOffset now)
    {
        var d = t - now;
        if (d <= TimeSpan.Zero) return "now";
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m";
        return $"{(int)d.TotalSeconds}s";
    }

    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
