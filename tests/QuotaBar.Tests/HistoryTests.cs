namespace QuotaBar.Tests;

[Collection("data")]
public class HistoryTests
{
    [Fact]
    public void Records_changes_and_skips_duplicates()
    {
        using var tmp = new TempData();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var reset = t0.AddHours(4);
        History.Record(Make.Report(Make.Provider("claude", t0, Make.Window("five_hour", 10, reset))), t0);
        // Same value a few minutes later: not written again.
        History.Record(Make.Report(Make.Provider("claude", t0.AddMinutes(5), Make.Window("five_hour", 10.2, reset))), t0.AddMinutes(5));
        // Changed value: written.
        History.Record(Make.Report(Make.Provider("claude", t0.AddMinutes(9), Make.Window("five_hour", 25, reset))), t0.AddMinutes(9));

        var pts = History.Load("claude", "five_hour", t0.AddHours(-1));
        Assert.Equal(new[] { 10.0, 25.0 }, pts.Select(p => p.Used));
        Assert.All(pts, p => Assert.Equal(reset.ToUnixTimeSeconds(), p.ResetsAt));
    }

    [Fact]
    public void Heartbeat_keeps_the_line_going()
    {
        using var tmp = new TempData();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        History.Record(Make.Report(Make.Provider("codex", t0, Make.Window("primary", 30, null))), t0);
        History.Record(Make.Report(Make.Provider("codex", t0.AddMinutes(45), Make.Window("primary", 30, null))), t0.AddMinutes(45));
        Assert.Equal(2, History.Load("codex", "primary", t0.AddHours(-1)).Count);
    }

    [Fact]
    public void Stale_cached_readings_and_errors_are_not_recorded()
    {
        using var tmp = new TempData();
        var now = DateTimeOffset.UtcNow;
        var stale = Make.Provider("claude", now.AddHours(-1), Make.Window("five_hour", 50, null));
        var failed = Make.Provider("codex", now, Make.Window("primary", 50, null));
        failed.Status = ProviderStatus.Error;
        History.Record(Make.Report(stale, failed), now);
        Assert.Empty(History.Load("claude", "five_hour", now.AddDays(-1)));
        Assert.Empty(History.Load("codex", "primary", now.AddDays(-1)));
    }

    [Fact]
    public void Survives_a_restart()
    {
        using var tmp = new TempData();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        History.Record(Make.Report(Make.Provider("claude", t0, Make.Window("five_hour", 40, null))), t0);
        History.ResetCache(); // app restarted
        History.Record(Make.Report(Make.Provider("claude", t0.AddMinutes(2), Make.Window("five_hour", 40, null))), t0.AddMinutes(2));
        Assert.Single(History.Load("claude", "five_hour", t0.AddHours(-1)));
    }
}
