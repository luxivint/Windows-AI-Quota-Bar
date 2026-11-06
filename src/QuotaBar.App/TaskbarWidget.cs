using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuotaBar.App;

/// <summary>
/// Small always-on-top strip that sits on the taskbar, just left of the tray icons
/// (Windows 11 has no deskbands, so this is an owned, borderless topmost window).
/// Two styles: "rings" (donut gauges with the provider mark inside) and "bars".
/// </summary>
public sealed class TaskbarWidget : Window
{
    readonly StackPanel _rows = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Border _card;
    readonly DispatcherTimer _keeper = new() { Interval = TimeSpan.FromSeconds(1.5) };
    IntPtr _hwnd, _tray, _fgHook, _shellHook;
    uint _shellPid;
    // Kept in a field so the GC does not collect the callback while Windows still calls it.
    readonly Native.WinEventProc _onWinEvent;
    Point? _dragStart;
    int _dragStartOffset, _count = 2;
    bool _dragging, _hover;

    public const string Primary = "main";
    /// <summary>"main" for the primary taskbar, "mon:x,y" (monitor origin) for a secondary one.</summary>
    public string TaskbarId { get; }

    /// <summary>Ids of the taskbars on other monitors (Shell_SecondaryTrayWnd).</summary>
    public static List<string> SecondaryTaskbars()
    {
        var ids = new List<string>();
        for (var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            if (Native.MonitorOrigin(h) is { } o) ids.Add($"mon:{o.X},{o.Y}");
        return ids;
    }

    IntPtr FindTray()
    {
        if (TaskbarId == Primary) return Native.FindWindow("Shell_TrayWnd", null);
        for (var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null); h != IntPtr.Zero;
             h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null))
            if (Native.MonitorOrigin(h) is { } o && $"mon:{o.X},{o.Y}" == TaskbarId) return h;
        return IntPtr.Zero;
    }

    public TaskbarWidget(string taskbarId = Primary)
    {
        TaskbarId = taskbarId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable but invisible
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 180; Height = 40;
        Left = -32000; Top = -32000; // off-screen until positioned on the taskbar
        WindowStartupLocation = WindowStartupLocation.Manual;
        _card = new Border { Child = _rows, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(2, 0, 2, 0) };
        Content = _card;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Cursor = Cursors.Hand;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            _tray = FindTray();
            if (_tray != IntPtr.Zero) new WindowInteropHelper(this).Owner = _tray; // stays above the taskbar
            Reposition();
        };
        // React at once when the foreground window changes (full-screen app starts or ends) or the shell
        // reorders its windows (clicking the clock or the tray brings the taskbar to the front, over us).
        _onWinEvent = OnWinEvent;
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _onWinEvent, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _keeper.Tick += (_, _) => Reposition();
        _keeper.Start();

        MouseEnter += (_, _) => { _hover = true; Paint(); };
        MouseLeave += (_, _) => { _hover = false; Paint(); };
        MouseLeftButtonDown += (_, e) => { _dragStart = PointToScreen(e.GetPosition(this)); _dragStartOffset = App.Instance.Settings.WidgetOffset; _dragging = false; CaptureMouse(); };
        MouseMove += OnDragMove;
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            if (_dragging) App.Instance.Settings.Save();
            else App.Instance.TogglePanel();
            _dragStart = null; _dragging = false;
        };
        MouseRightButtonUp += (_, _) => ShowMenu();

        App.Instance.ReportChanged += Render;
        App.Instance.SettingsChanged += Render;
        Theme.Changed += Render;
        Closed += (_, _) =>
        {
            _keeper.Stop();
            if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
            if (_shellHook != IntPtr.Zero) Native.UnhookWinEvent(_shellHook);
            App.Instance.ReportChanged -= Render; App.Instance.SettingsChanged -= Render; Theme.Changed -= Render;
        };
        Render();
    }

    bool Bars => App.Instance.Settings.WidgetStyle == "bars";
    double Scale => App.Instance.Settings.WidgetSize switch { "compact" => 0.9, "large" => 1.1, _ => 1.0 };
    double ContentWidth => (Bars ? 204 : _count * 84 + 10) * Scale;

    /// <summary>Fill for a limit, following Settings → Taskbar → Colors.</summary>
    Brush Fill(string kind, double left, Brush fg)
    {
        switch (App.Instance.Settings.WidgetColor)
        {
            case "status":
                return left <= 10 ? Theme.Bad : left <= 25 ? Theme.Warn : Theme.Good;
            case "mono":
                return left <= 10 ? Theme.Bad : left <= 25 ? Theme.Warn : fg;
            case "accent":
                return left <= 10 ? Theme.Bad : left <= 25 ? Theme.Warn : SystemParameters.WindowGlassBrush ?? Theme.Accent;
            default:
                return Visuals.LeftBrush(kind, left);
        }
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        var dx = now.X - start.X;
        if (!_dragging && Math.Abs(dx) < 6) return;
        _dragging = true;
        App.Instance.Settings.WidgetOffset = Math.Max(0, _dragStartOffset - (int)dx);
        Reposition();
    }

    void ShowMenu()
    {
        var s = App.Instance.Settings;
        var m = new ContextMenu();
        void Add(string text, Action a, bool check = false) { var i = new MenuItem { Header = text, IsChecked = check }; i.Click += (_, _) => a(); m.Items.Add(i); }
        Add(L.T("Open Windows AI Quota Bar", "Windows AI Quota Bar'ı aç"), () => App.Instance.ShowPanel());
        Add(L.T("Refresh now", "Şimdi yenile"), () => _ = App.Instance.RefreshAsync());
        m.Items.Add(new Separator());
        Add(L.T("Style: rings", "Görünüm: halkalar"), () => { s.WidgetStyle = "rings"; App.Instance.SaveSettings(); }, !Bars);
        Add(L.T("Style: bars", "Görünüm: çubuklar"), () => { s.WidgetStyle = "bars"; App.Instance.SaveSettings(); }, Bars);
        Add(L.T("Choose what to show…", "Neyin gösterileceğini seç…"), () => App.Instance.ShowPanel("settings"));
        Add(L.T("Reset position", "Konumu sıfırla"), () => { s.WidgetOffset = 0; s.Save(); Reposition(); });
        m.Items.Add(new Separator());
        Add(L.T("Hide from taskbar", "Görev çubuğundan gizle"), () => { s.ShowTaskbarWidget = false; App.Instance.SaveSettings(); });
        m.IsOpen = true;
    }

    void Paint()
    {
        var dark = Theme.TaskbarDark;
        _card.Background = new SolidColorBrush(_hover
            ? (dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0))
            : (dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0)));
        _card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
    }

    void Render()
    {
        _rows.Children.Clear();
        Paint();
        var app = App.Instance;
        var metrics = Metrics.ForWidget(app.Report, app.Settings);
        _count = Math.Max(1, metrics.Count);
        var dark = Theme.TaskbarDark;
        Brush fg = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(20, 20, 20));
        Brush dim = new SolidColorBrush(dark ? Color.FromArgb(165, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0));
        Brush track = new SolidColorBrush(dark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(38, 0, 0, 0));

        _rows.LayoutTransform = Scale == 1.0 ? Transform.Identity : new ScaleTransform(Scale, Scale);
        if (metrics.Count == 0)
        {
            _rows.Orientation = Orientation.Horizontal;
            _rows.HorizontalAlignment = HorizontalAlignment.Center;
            _rows.Children.Add(new TextBlock { Text = app.Refreshing ? "AI Quota Bar…" : "AI Quota Bar", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var now = DateTimeOffset.Now;
            if (Bars) RenderBars(metrics, fg, dim, track, now);
            else RenderRings(metrics, fg, dim, track, now);
            ToolTip = string.Join("\n", metrics.Select(m =>
                $"{m.Provider.DisplayName}{(m.Provider.Account is { } a ? " (" + a + ")" : "")} · {PanelWindow.WindowLabel(m.Window.Label)}: {m.Window.LeftPercent:0}% {L.T("left", "kaldı")}" +
                (m.Window.ResetsAt is { } rr ? $", {L.T("resets in", "sıfırlanma")} {L.Until(rr, now)}" : "")));
        }
        Reposition();
    }

    void RenderRings(List<Metrics.Metric> metrics, Brush fg, Brush dim, Brush track, DateTimeOffset now)
    {
        _rows.Orientation = Orientation.Horizontal;
        _rows.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var m in metrics)
        {
            var kind = m.Provider.ProviderKind;
            var icon = Visuals.Icon(kind, 12, fg);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            var ring = Visuals.Ring(m.Window.LeftPercent, 27, 3.2, kind, icon, track, Fill(kind, m.Window.LeftPercent, fg));
            ring.VerticalAlignment = VerticalAlignment.Center;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Width = 42 };
            text.Children.Add(new TextBlock
            {
                Text = $"{m.Window.LeftPercent:0}%", Foreground = fg, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            });
            text.Children.Add(new TextBlock
            {
                Text = m.Window.ResetsAt is { } r ? "↻ " + Short(L.Until(r, now)) : ShortLabel(m.Window.Label),
                Foreground = dim, FontSize = 10, LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            });
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 5, 0) };
            cell.Children.Add(ring);
            cell.Children.Add(text);
            _rows.Children.Add(cell);
        }
    }

    void RenderBars(List<Metrics.Metric> metrics, Brush fg, Brush dim, Brush track, DateTimeOffset now)
    {
        _rows.Orientation = Orientation.Vertical;
        _rows.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var m in metrics)
        {
            var row = new Grid { Height = 15, Margin = new Thickness(8, 0, 8, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            var kind = m.Provider.ProviderKind;
            var icon = Visuals.Icon(kind, 12, fg);
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);

            var left = m.Window.LeftPercent;
            var barGrid = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
            barGrid.Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(3) });
            var fill = new Border { Background = Fill(kind, left, fg), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            barGrid.Children.Add(fill);
            barGrid.SizeChanged += (_, _) => fill.Width = Math.Max(left > 0 ? 6 : 0, barGrid.ActualWidth * Math.Clamp(left, 0, 100) / 100);
            Grid.SetColumn(barGrid, 1); row.Children.Add(barGrid);

            var pct = new TextBlock { Text = $"{left:0}%", Foreground = fg, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pct, 2); row.Children.Add(pct);
            var reset = new TextBlock { Text = m.Window.ResetsAt is { } r ? Short(L.Until(r, now)) : "", Foreground = dim, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(reset, 3); row.Children.Add(reset);
            _rows.Children.Add(row);
        }
    }

    /// <summary>"6d 3h" → "6d", "4h 55m" → "4h".</summary>
    static string Short(string s) => s.Split(' ')[0];
    static string ShortLabel(string label) => PanelWindow.WindowLabel(label).Split(' ')[0];

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == Native.EVENT_SYSTEM_FOREGROUND) { Reposition(); return; }
        if (idObject != 0 /* OBJID_WINDOW */) return;
        if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE) { if (hwnd == _tray) Reposition(); return; } // auto-hide slide
        // Taskbar or a shell flyout was shown/reordered: get back on top of the taskbar right away.
        if (_hwnd != IntPtr.Zero && Native.IsWindowVisible(_hwnd) && Native.IsAbove(_tray, _hwnd))
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Listens to the shell process (explorer) only; re-attached when explorer restarts.</summary>
    void HookShell()
    {
        var pid = Native.ProcessOf(_tray);
        if (pid == 0 || pid == _shellPid) return;
        if (_shellHook != IntPtr.Zero) Native.UnhookWinEvent(_shellHook);
        _shellPid = pid;
        _shellHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _onWinEvent, pid, 0,
            Native.WINEVENT_OUTOFCONTEXT);
    }

    void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out _)) _tray = FindTray();
        if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out var bar))
        {
            // A secondary taskbar went away (monitor unplugged, explorer restarting): stay hidden until it is back.
            if (Native.IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, 0);
            return;
        }
        HookShell();

        // Hidden together with the taskbar (full-screen game/video, auto-hide); back as soon as the taskbar is.
        // Use Win32 visibility directly: WPF's Show/Hide state can disagree with the real window during startup.
        if (Native.TaskbarHidden(_tray)) { if (Native.IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, 0 /* SW_HIDE */); return; }

        var dpi = Native.GetDpiForWindow(_tray) / 96.0;
        if (dpi <= 0) dpi = 1;
        int w = (int)(ContentWidth * dpi);
        bool horizontal = bar.Width > bar.Height;
        int h = horizontal ? bar.Height : (int)(40 * dpi);

        // Right edge = left edge of the notification area (tray icons + clock). Secondary taskbars only have the clock.
        int rightEdge = bar.Right - (int)((TaskbarId == Primary ? 260 : 120) * dpi);
        var notify = Native.FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Width > 0) rightEdge = nr.Left;
        int x = rightEdge - w - (int)(App.Instance.Settings.WidgetOffset * dpi) - (int)(6 * dpi);
        x = Math.Max(bar.Left, x);
        int y = horizontal ? bar.Top : bar.Bottom - h;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}
