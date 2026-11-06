namespace QuotaBar.Tests;

public class AlertsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    const long H5 = 5 * 3600, D7 = 7 * 86400;

    static List<Alert> Run(UsageReport? prev, UsageReport cur, AlertState? state = null, AlertSettings? s = null) =>
        Alerts.Evaluate(prev, cur, s ?? new AlertSettings(), state ?? new AlertState(), Now);

    [Fact]
    public void Threshold_fires_once_per_cycle()
    {
        var state = new AlertState();
        var only = new AlertSettings { RunOut = false };
        var reset = Now.AddHours(3);
        var r = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 82, reset, H5)));
        var first = Run(null, r, state, only);
        Assert.Single(first);
        Assert.Equal(AlertKind.Threshold, first[0].Kind);
        Assert.Equal(80, first[0].Value);

        // Same cycle (reset time drifted a few seconds): nothing new.
        var again = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 85, reset.AddSeconds(20), H5)));
        Assert.Empty(Run(r, again, state, only));

        // Next threshold in the same cycle fires.
        var higher = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 96, reset, H5)));
        var h = Run(again, higher, state, only);
        Assert.Single(h);
        Assert.Equal(95, h[0].Value);
    }

    [Fact]
    public void Jumping_past_two_thresholds_sends_one_notification()
    {
        var state = new AlertState();
        var r = Make.Report(Make.Provider("codex", Now, Make.Window("primary", 97, Now.AddHours(1), H5)));
        var a = Run(null, r, state);
        Assert.Single(a);
        Assert.Equal(95, a[0].Value);
        Assert.Empty(Run(r, r, state));
    }

    [Fact]
    public void New_cycle_fires_again()
    {
        var state = new AlertState();
        Run(null, Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 82, Now.AddHours(1), H5))), state);
        var next = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 81, Now.AddHours(6), H5)));
        Assert.Single(Run(null, next, state));
    }

    [Fact]
    public void Reset_detected_from_previous_reading()
    {
        var prev = Make.Report(Make.Provider("claude", Now.AddMinutes(-5), Make.Window("five_hour", 70, Now.AddMinutes(-1), H5)));
        var cur = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 0, Now.AddHours(5), H5)));
        var a = Run(prev, cur);
        Assert.Single(a);
        Assert.Equal(AlertKind.Reset, a[0].Kind);
    }

    [Fact]
    public void Low_usage_reset_is_not_news()
    {
        var prev = Make.Report(Make.Provider("claude", Now.AddMinutes(-5), Make.Window("five_hour", 20, Now.AddMinutes(-1), H5)));
        var cur = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 0, Now.AddHours(5), H5)));
        Assert.Empty(Run(prev, cur));
    }

    [Fact]
    public void Run_out_warning_when_close()
    {
        // 4 h into a 5 h window at 70%: 17.5%/h → full in ~1.7 h? No: (100-70)/17.5 = 1.71 h, after the reset (1 h). No alert.
        var calm = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 70, Now.AddHours(1), H5)));
        Assert.Empty(Run(null, calm, s: new AlertSettings { Thresholds = new() { 99 } }));

        // 1 h into the window at 60%: 60%/h → full in 40 min, reset in 4 h.
        var fast = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 60, Now.AddHours(4), H5)));
        var a = Run(null, fast, s: new AlertSettings { Thresholds = new() { 99 } });
        Assert.Single(a);
        Assert.Equal(AlertKind.RunOut, a[0].Kind);
        Assert.InRange((a[0].At!.Value - Now).TotalMinutes, 39, 41);
    }

    [Fact]
    public void Unused_quota_warning_for_weekly_windows_only()
    {
        var weekly = Make.Report(Make.Provider("codex", Now, Make.Window("secondary", 40, Now.AddHours(3), D7)));
        var a = Run(null, weekly);
        Assert.Single(a);
        Assert.Equal(AlertKind.Unused, a[0].Kind);
        Assert.Equal(60, a[0].Value);

        var session = Make.Report(Make.Provider("codex", Now, Make.Window("primary", 10, Now.AddHours(3), H5)));
        Assert.Empty(Run(null, session));
    }

    [Fact]
    public void Muted_disabled_and_stale_providers_are_quiet()
    {
        var r = Make.Report(Make.Provider("claude", Now, Make.Window("five_hour", 90, Now.AddHours(1), H5)));
        Assert.Empty(Run(null, r, s: new AlertSettings { Muted = new() { "claude" } }));
        Assert.Empty(Run(null, r, s: new AlertSettings { Enabled = false }));
        var stale = Make.Report(Make.Provider("claude", Now.AddHours(-2), Make.Window("five_hour", 90, Now.AddHours(1), H5)));
        Assert.Empty(Run(null, stale));
    }
}
