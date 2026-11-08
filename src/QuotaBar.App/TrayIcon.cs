using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuotaBar.App;

public static class Metrics
{
    public sealed record Metric(string Id, ProviderSnapshot Provider, UsageWindow Window)
    {
        public string Title => $"{Visuals.ShortName(Provider)} · {L.Window(Window.Label)}";
    }

    /// <summary>"providerId|windowKey". providerId "antigravity" means "whichever account is active".</summary>
    public static Metric? Resolve(UsageReport? r, string id)
    {
        if (r is null) return null;
        var parts = id.Split('|');
        if (parts.Length != 2) return null;
        var p = r.Providers.FirstOrDefault(x => x.Id == parts[0])
             ?? r.Providers.FirstOrDefault(x => x.ProviderKind == parts[0] && x.IsActiveAccount && x.Status == ProviderStatus.Ok);
        var w = p?.Windows.FirstOrDefault(x => x.Key == parts[1]);
        return p is null || w is null ? null : new Metric(id, p, w);
    }

    public static IEnumerable<Metric> All(UsageReport? r)
    {
        if (r is null) yield break;
        foreach (var p in r.Providers.Where(p => p.Status == ProviderStatus.Ok))
            foreach (var w in p.Windows)
            {
                var pid = p.ProviderKind is "antigravity" or "codex" && p.IsActiveAccount ? p.ProviderKind : p.Id;
                yield return new Metric($"{pid}|{w.Key}", p, w);
            }
    }

    /// <summary>
    /// The two limits that need attention most, from different providers: a projected run-out first
    /// (soonest), then the least left.
    /// </summary>
    public static List<Metric> Urgent(UsageReport? r, DateTimeOffset now) =>
        All(r).Where(m => m.Provider.IsActiveAccount || m.Provider.ProviderKind is not ("antigravity" or "codex"))
              .OrderBy(m => m.Window.ProjectedRunOut(now) is { } ro ? (ro - now).TotalHours : double.MaxValue)
              .ThenBy(m => m.Window.LeftPercent)
              .GroupBy(m => m.Provider.Id).Select(g => g.First())
              .Take(2).ToList();

    /// <summary>Configured widget metrics, falling back to the first sensible ones.</summary>
    public static List<Metric> ForWidget(UsageReport? r, AppSettings s)
    {
        if (s.WidgetAuto) return Urgent(r, DateTimeOffset.Now);
        var list = s.WidgetMetrics.Select(id => Resolve(r, id)).OfType<Metric>().Take(2).ToList();
        if (list.Count == 0) list = All(r).GroupBy(m => m.Provider.Id).Select(g => g.First()).Take(2).ToList();
        return list;
    }
}

public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    IntPtr _lastHicon;

    public TrayIcon()
    {
        _icon = new NotifyIcon { Visible = true, Text = "Windows AI Quota Bar" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.Instance.TogglePanel(); };
        _icon.BalloonTipClicked += (_, _) => App.Instance.ShowPanel();
        BuildMenu();
        App.Instance.ReportChanged += Update;
        Theme.Changed += Update;
        Update();
    }

    public void BuildMenu()
    {
        var old = _icon.ContextMenuStrip;
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Open Windows AI Quota Bar", "Windows AI Quota Bar'ı aç"), null, (_, _) => App.Instance.ShowPanel());
        menu.Items.Add(L.T("Refresh now", "Şimdi yenile"), null, (_, _) => _ = App.Instance.RefreshAsync());
        menu.Items.Add(L.T("Settings", "Ayarlar"), null, (_, _) => App.Instance.ShowPanel("settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Quit", "Çık"), null, (_, _) => App.Instance.Quit());
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    void Update()
    {
        var app = App.Instance;
        var metrics = Metrics.ForWidget(app.Report, app.Settings);
        var tip = metrics.Count == 0 ? "Windows AI Quota Bar" : "Windows AI Quota Bar\n" + string.Join("\n", metrics.Select(m => m.Title + ": " + L.F("{0:0}% left", "%{0:0} kaldı", m.Window.LeftPercent)));
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        using var bmp = Render(metrics.Select(m => m.Window.LeftPercent).ToList());
        var h = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(h);
        if (_lastHicon != IntPtr.Zero) DestroyIcon(_lastHicon);
        _lastHicon = h;
    }

    static Bitmap Render(List<double> lefts)
    {
        const int S = 32;
        var bmp = new Bitmap(S, S);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fg = Theme.TaskbarDark ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(20, 20, 20);
        var track = System.Drawing.Color.FromArgb(Theme.TaskbarDark ? 90 : 60, fg);
        if (lefts.Count == 0) lefts = new List<double> { 100, 100 };
        var rows = lefts.Take(2).ToList();
        int barH = rows.Count == 1 ? 10 : 9, gap = 5;
        int total = rows.Count * barH + (rows.Count - 1) * gap;
        int y = (S - total) / 2;
        foreach (var left in rows)
        {
            Pill(g, new SolidBrush(track), 1, y, S - 2, barH);
            var color = left <= 10 ? System.Drawing.Color.FromArgb(229, 72, 77) : left <= 25 ? System.Drawing.Color.FromArgb(242, 157, 11) : fg;
            var w = Math.Max(barH, (int)Math.Round((S - 2) * Math.Clamp(left, 0, 100) / 100));
            if (left > 0) Pill(g, new SolidBrush(color), 1, y, w, barH);
            y += barH + gap;
        }
        return bmp;
    }

    static void Pill(Graphics g, System.Drawing.Brush b, int x, int y, int w, int h)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, h, h, 90, 180);
        path.AddArc(x + w - h, y, h, h, 270, 180);
        path.CloseFigure();
        g.FillPath(b, path);
        b.Dispose();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>Windows notification from the tray icon (clicking it opens the panel).</summary>
    public void Notify(string title, string text)
    {
        try { _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_lastHicon != IntPtr.Zero) DestroyIcon(_lastHicon);
    }
}
