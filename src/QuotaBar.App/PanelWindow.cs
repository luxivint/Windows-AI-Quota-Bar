using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using QuotaBar.Core.Providers;

namespace QuotaBar.App;

/// <summary>The main popup (tray) / pinned window.</summary>
public sealed class PanelWindow : Window
{
    string _page = "overview";
    bool _pinned;
    public bool JustHidden { get; private set; }

    readonly Grid _root = new();
    readonly StackPanel _sidebarTop = new();
    readonly StackPanel _sidebarBottom = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(20, 18, 20, 8) };
    readonly TextBlock _footLeft = new(), _footRight = new();
    readonly Border _sidebar = new();
    readonly Grid _footer = new();
    readonly Border _updateBar = new() { Visibility = Visibility.Collapsed };
    readonly Button _pinBtn, _refreshBtn;

    public PanelWindow()
    {
        Title = "Windows AI Quota Bar";
        Width = 420; Height = 660; MinWidth = 360; MinHeight = 420;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/QuotaBar;component/Assets/quotabar.ico"));
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        _root.ColumnDefinitions.Add(new ColumnDefinition());

        var side = new Grid();
        side.Children.Add(_sidebarTop);
        side.Children.Add(_sidebarBottom);
        _sidebar.Child = side;
        _root.Children.Add(_sidebar);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(_scroll);
        Grid.SetRow(_updateBar, 1);
        main.Children.Add(_updateBar);
        _footer.Margin = new Thickness(20, 6, 12, 10);
        _footer.ColumnDefinitions.Add(new ColumnDefinition());
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footLeft.FontSize = 12.5; _footRight.FontSize = 12.5;
        _footLeft.TextTrimming = _footRight.TextTrimming = TextTrimming.CharacterEllipsis;
        _footLeft.VerticalAlignment = _footRight.VerticalAlignment = VerticalAlignment.Center;
        _footer.Children.Add(_footLeft);
        Grid.SetColumn(_footRight, 1); _footer.Children.Add(_footRight);
        _refreshBtn = IconButton("", L.T("Refresh now", "Şimdi yenile"), () => { App.Instance.RefreshCodex(); _ = App.Instance.RefreshAsync(); });
        Grid.SetColumn(_refreshBtn, 2); _footer.Children.Add(_refreshBtn);
        _pinBtn = IconButton("", L.T("Pin as a normal window", "Normal pencere olarak sabitle"), TogglePin);
        Grid.SetColumn(_pinBtn, 3); _footer.Children.Add(_pinBtn);
        Grid.SetRow(_footer, 2);
        main.Children.Add(_footer);
        Grid.SetColumn(main, 1);
        _root.Children.Add(main);
        Content = _root;

        // Drag the window by its empty areas.
        // Pinned window: drag it by the empty part of the sidebar or the footer. Never on content, so clicks are not swallowed.
        System.Windows.Input.MouseButtonEventHandler drag = (_, e) => { if (_pinned && !e.Handled && e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        _sidebar.MouseLeftButtonDown += drag;
        _footer.MouseLeftButtonDown += drag;
        Deactivated += async (_, _) =>
        {
            if (_pinned || !IsVisible) return;
            await Task.Delay(120);
            // Our own dialogs (message boxes, login window) should not close the popup.
            if (Native.ForegroundIsOurs() || _pinned) return;
            Hide();
            JustHidden = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            t.Tick += (_, _) => { JustHidden = false; t.Stop(); };
            t.Start();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_pinned) Hide(); };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.Win11Chrome(h, Theme.Dark);
            _mica = Native.TryMica(h);
            if (_mica) HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;
            Rebuild();
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); };

        App.Instance.ReportChanged += OnReportChanged;
        App.Instance.SettingsChanged += () => { if (IsVisible && _page != "settings") Rebuild(); else _dirty = true; };
        Theme.Changed += () => { Native.Win11Chrome(new WindowInteropHelper(this).Handle, Theme.Dark); Rebuild(); };

        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => { if (IsVisible) UpdateFooter(); };
        tick.Start();

        _page = App.Instance.Settings.LastSelectedProvider ?? "overview";
        Rebuild();
    }

    UsageReport? _shownReport;
    bool _mica;
    bool _dirty;

    /// <summary>Only rebuild when the data actually changed and the panel is visible; otherwise just tick the footer.</summary>
    void OnReportChanged()
    {
        var app = App.Instance;
        if (ReferenceEquals(app.Report, _shownReport)) { UpdateFooter(); return; }
        if (!IsVisible) { _dirty = true; return; }
        Rebuild();
    }

    public void Navigate(string page)
    {
        var changed = page != _page;
        _page = page;
        if (changed) _scroll.ScrollToTop();
        if (page != "settings") { App.Instance.Settings.LastSelectedProvider = page; try { App.Instance.Settings.Save(); } catch { } }
        Rebuild();
    }

    void TogglePin()
    {
        _pinned = !_pinned;
        Topmost = !_pinned;
        ShowInTaskbar = _pinned;
        Rebuild();
        Activate();
    }

    public void ShowNearTray()
    {
        if (!_pinned)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 12;
            Top = wa.Bottom - Height - 12;
        }
        Show();
        Activate();
        if (_dirty || !ReferenceEquals(_shownReport, App.Instance.Report)) Rebuild();
    }

    // ---------------------------------------------------------------------------------------------

    void Rebuild()
    {
        var searchCaret = Keyboard.FocusedElement is TextBox { Name: "CodexAccountSearch" } searchBox ? searchBox.CaretIndex : -1;
        if (_page == "codex" || _page.StartsWith("codex:")) _page = "codex-accounts";
        var accounts = _page == "codex-accounts";
        _scroll.VerticalScrollBarVisibility = accounts ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        _shownReport = App.Instance.Report;
        _dirty = false;
        Background = _mica ? Brushes.Transparent : Theme.Bg;
        _sidebar.Background = _mica ? Theme.SidebarGlass : Theme.Sidebar;
        _sidebar.BorderBrush = Theme.Border;
        _sidebar.BorderThickness = new Thickness(0, 0, 1, 0);
        _footLeft.Foreground = _footRight.Foreground = Theme.SubText;
        _footer.Background = Brushes.Transparent;
        BuildSidebar();

        var report = App.Instance.Report;
        if (_page != "overview" && _page != "settings" && _page != "codex-accounts" && report?.Providers.Any(p => p.Id == _page) != true)
            _page = "overview";

        _scroll.Content = _page switch
        {
            "settings" => SettingsPage.Build(this),
            "codex-accounts" => CodexAccountsPage.Build(this),
            "overview" => BuildOverview(report),
            _ => BuildProvider(report!.Providers.First(p => p.Id == _page)),
        };
        if (accounts && searchCaret >= 0 && IsVisible)
            Dispatcher.BeginInvoke(new Action(() => { if (_page == "codex-accounts" && IsVisible) CodexAccountsPage.RestoreSearchFocus(searchCaret); }));
        ((TextBlock)((Button)_pinBtn).Content).Text = _pinned ? "" : "";
        _pinBtn.ToolTip = _pinned ? L.T("Unpin (back to tray popup)", "Sabitlemeyi kaldır") : L.T("Pin as a normal window", "Normal pencere olarak sabitle");
        UpdateFooter();
    }

    string? _barState;

    /// <summary>"Update available" strip above the footer.</summary>
    void UpdateUpdateBar()
    {
        var app = App.Instance;
        var u = app.Update;
        var state = u is null ? null : $"{u.Tag}|{app.UpdateStatus}|{app.UpdateBusy}|{Theme.Dark}";
        if (state == _barState) return;
        _barState = state;
        if (u is null) { _updateBar.Visibility = Visibility.Collapsed; return; }

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Visuals.Label(L.F("Version {0} is available", "{0} sürümü hazır", u.Version), 12.5, Theme.Text, FontWeights.SemiBold));
        var sub = Visuals.Label(app.UpdateStatus ?? L.T("See what's new", "Yenilikleri gör"), 11.5, app.UpdateStatus is null ? Theme.Accent : Theme.SubText);
        if (app.UpdateStatus is null) { sub.Cursor = Cursors.Hand; sub.MouseLeftButtonDown += (_, e) => { e.Handled = true; App.OpenUrl(u.PageUrl); }; }
        text.Children.Add(sub);
        g.Children.Add(text);
        if (!app.UpdateBusy)
        {
            var btn = Visuals.Chip(Updates.CanSelfUpdate ? L.T("Update", "Güncelle") : L.T("Download", "İndir"), () => _ = app.InstallUpdateAsync(), primary: true);
            btn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(btn, 1); g.Children.Add(btn);
        }
        _updateBar.Child = g;
        _updateBar.Margin = new Thickness(14, 4, 12, 0);
        _updateBar.Padding = new Thickness(12, 8, 8, 8);
        _updateBar.CornerRadius = new CornerRadius(10);
        _updateBar.BorderThickness = new Thickness(1);
        _updateBar.BorderBrush = Theme.Accent;
        _updateBar.Background = new SolidColorBrush(Color.FromArgb((byte)(Theme.Dark ? 40 : 26), ((SolidColorBrush)Theme.Accent).Color.R, ((SolidColorBrush)Theme.Accent).Color.G, ((SolidColorBrush)Theme.Accent).Color.B));
        _updateBar.Visibility = Visibility.Visible;
    }

    void UpdateFooter()
    {
        UpdateUpdateBar();
        var app = App.Instance;
        var now = DateTimeOffset.Now;
        _footLeft.Text = app.Refreshing ? L.T("Updating…", "Güncelleniyor…")
            : app.LastRefresh is { } t ? L.T("Updated ", "Güncellendi: ") + Ago(t, now) : "";
        _footRight.Text = app.Refreshing ? "" : L.T("Next update in ", "Sonraki: ") + L.Until(app.NextRefresh, now);
        _footRight.Margin = new Thickness(8, 0, 6, 0);
    }

    static string Ago(DateTimeOffset t, DateTimeOffset now) => L.Ago(t, now);

    void BuildSidebar()
    {
        _sidebarTop.Children.Clear();
        _sidebarBottom.Children.Clear();
        _sidebarTop.Margin = new Thickness(0, 10, 0, 0);
        _sidebarTop.Children.Add(SideItem("overview", Visuals.Glyph("", 20), L.T("Overview", "Genel bakış")));
        var report = App.Instance.Report;
        if (report != null)
        {
            foreach (var p in report.Providers.Where(p => p.Status != ProviderStatus.Disabled)
                .DistinctBy(p => p.ProviderKind == "codex" ? "codex" : p.Id))
            {
                if (p.ProviderKind == "codex")
                {
                    _sidebarTop.Children.Add(SideItem("codex-accounts", Visuals.Icon("codex", 22),
                        L.T("Codex accounts", "Codex hesapları")));
                    continue;
                }
                var icon = Visuals.Icon(p.ProviderKind, 22);
                FrameworkElement content = icon;
                if (p.ProviderKind is "antigravity" or "opencode-go" or "codex" && report.Providers.Count(x => x.ProviderKind == p.ProviderKind) > 1)
                {
                    var g = new Grid();
                    g.Children.Add(icon);
                    var badge = new Border
                    {
                        Background = Theme.Bg, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -7, -6),
                        Padding = new Thickness(3, 0, 3, 0),
                        Child = Visuals.Label((Visuals.ShortName(p).FirstOrDefault(char.IsLetterOrDigit).ToString()).ToUpperInvariant(), 9, Theme.Text, FontWeights.Bold),
                    };
                    g.Children.Add(badge);
                    content = g;
                }
                if (App.Instance.HealthOf(p.ProviderKind) is { } sh)
                {
                    var g2 = new Grid();
                    g2.Children.Add(content);
                    g2.Children.Add(new Border
                    {
                        Width = 9, Height = 9, CornerRadius = new CornerRadius(5), BorderBrush = Theme.Sidebar, BorderThickness = new Thickness(1.5),
                        Background = sh.Health >= Health.Major ? Theme.Bad : Theme.Warn,
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, -4, 0),
                    });
                    content = g2;
                }
                var tip = p.DisplayName + (p.Account is null ? "" : " — " + p.Account)
                    + (App.Instance.HealthOf(p.ProviderKind) is { } th ? Environment.NewLine + "⚠ " + th.Description : "");
                var item = SideItem(p.Id, content, tip);
                if (p.Status != ProviderStatus.Ok) item.Opacity = 0.5;
                _sidebarTop.Children.Add(item);
            }
        }
        _sidebarBottom.Children.Add(SideItem("help", Visuals.Glyph("", 19), L.T("Project page", "Proje sayfası"), () => App.OpenUrl(SettingsPage.RepoUrl)));
        _sidebarBottom.Children.Add(SideItem("settings", Visuals.Glyph("", 19), L.T("Settings", "Ayarlar")));
    }

    FrameworkElement SideItem(string page, FrameworkElement content, string tooltip, Action? click = null)
    {
        var selected = _page == page;
        var kind = page == "codex-accounts" ? "codex" : App.Instance.Report?.Providers.FirstOrDefault(x => x.Id == page)?.ProviderKind;
        var host = new Grid { Height = 44, Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = tooltip };
        var selBrush = kind != null ? Visuals.Tint(kind, (byte)(Theme.Dark ? 48 : 34)) : Theme.Selected;
        var bg = new Border { Margin = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(10), Background = selected ? selBrush : Brushes.Transparent };
        host.Children.Add(bg);
        if (selected) host.Children.Add(new Border { Width = 3, Height = 20, CornerRadius = new CornerRadius(2), Background = kind != null ? new SolidColorBrush(Visuals.BrandColor(kind)) : Theme.Text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) });
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        host.Children.Add(content);
        host.MouseEnter += (_, _) => { if (!selected) bg.Background = Theme.Hover; };
        host.MouseLeave += (_, _) => { if (!selected) bg.Background = Brushes.Transparent; };
        host.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (click != null) click(); else Navigate(page); };
        return host;
    }

    Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = Visuals.Glyph(glyph, 14, Theme.SubText), ToolTip = tip, Width = 28, Height = 28, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Template = Visuals.ChipTemplate(), Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(0),
        };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- pages -------------------------------------------------------------------------------------

    UIElement BuildOverview(UsageReport? report)
    {
        var sp = new StackPanel();
        sp.Children.Add(Visuals.Label(L.T("All subscriptions", "Tüm abonelikler"), 21, weight: FontWeights.SemiBold));
        sp.Children.Add(new Border { Height = 14 });
        if (report is null || report.Providers.Count == 0)
        {
            sp.Children.Add(Visuals.Label(App.Instance.Refreshing ? L.T("Loading…", "Yükleniyor…") : L.T("No data yet.", "Henüz veri yok."), 13, Theme.SubText));
            return sp;
        }
        var now = DateTimeOffset.Now;
        foreach (var p in report.Providers.Where(p => p.Status != ProviderStatus.Disabled)
            .Where(p => p.ProviderKind != "codex" || p == report.Providers.Where(x => x.ProviderKind == "codex").OrderByDescending(x => x.IsActiveAccount).First()))
        {
            var card = new Border
            {
                BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand, Background = Theme.Card,
            };
            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var head = new DockPanel();
            var ic = Visuals.Icon(p.ProviderKind, 18); ic.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(ic);
            if (p.Plan is { } plan)
            {
                var chip = PlanChip(plan); DockPanel.SetDock(chip, Dock.Right);
                head.Children.Add(chip);
            }
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(Visuals.Label(p.DisplayName, 14, weight: FontWeights.SemiBold));
            if (p.ProviderKind is "antigravity" or "codex" && p.IsActiveAccount)
                title.Children.Add(Visuals.Dot(Theme.Accent, 6));
            head.Children.Add(title);
            inner.Children.Add(head);
            if (p.ProviderKind == "codex")
                inner.Children.Add(Visuals.Label(L.F("{0} account(s)", "{0} hesap", report.Providers.Count(x => x.ProviderKind == "codex")), 11.5, Theme.SubText));
            if (p.Account != null && p.ProviderKind is "antigravity" or "opencode-go" or "codex")
            {
                var acc = Visuals.Label(p.Account, 11.5, Theme.SubText);
                acc.Margin = new Thickness(26, 0, 0, 0);
                inner.Children.Add(acc);
            }

            if (App.Instance.HealthOf(p.ProviderKind) is { } health) inner.Children.Add(IncidentLine(health, compact: true));
            if (p.Status != ProviderStatus.Ok)
            {
                var msg = Visuals.Label(p.Message ?? p.Status.ToString(), 12, p.Status == ProviderStatus.NeedsLogin ? Theme.Warn : Theme.Bad);
                msg.TextWrapping = TextWrapping.Wrap; msg.Margin = new Thickness(0, 6, 0, 0);
                inner.Children.Add(msg);
            }
            foreach (var w in p.Windows.Take(p.ProviderKind == "antigravity" ? 3 : 2))
            {
                var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                row.Children.Add(Visuals.Label(WindowLabel(w.Label), 12, Theme.SubText));
                var bar = Visuals.Bar(w.LeftPercent, PaceLeft(w, now), 6, p.ProviderKind); bar.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(bar, 1); row.Children.Add(bar);
                var pct = Visuals.Label($"{w.LeftPercent:0}%", 12, Theme.Text, FontWeights.SemiBold); pct.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(pct, 2); row.Children.Add(pct);
                inner.Children.Add(row);
            }
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.Children.Add(new Border { Background = new SolidColorBrush(Visuals.BrandColor(p.ProviderKind)), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 0, 2) });
            inner.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(inner, 1); layout.Children.Add(inner);
            if (p.Status == ProviderStatus.Ok && p.Windows.Count > 0)
            {
                var worst = p.Windows.Take(p.ProviderKind == "antigravity" ? 3 : 2).MinBy(x => x.LeftPercent)!;
                var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var big = Visuals.Label($"{worst.LeftPercent:0}", 14, Theme.Text, FontWeights.Bold); big.HorizontalAlignment = HorizontalAlignment.Center;
                center.Children.Add(big);
                var ring = Visuals.Ring(worst.LeftPercent, 54, 5, p.ProviderKind, center);
                ring.Margin = new Thickness(14, 0, 0, 0);
                ring.VerticalAlignment = VerticalAlignment.Center;
                ring.ToolTip = PanelWindow.WindowLabel(worst.Label) + " · " + L.T("lowest", "en düşük");
                Grid.SetColumn(ring, 2); layout.Children.Add(ring);
            }
            card.Child = layout;
            var id = p.Id;
            card.MouseEnter += (_, _) => card.Background = Theme.Hover;
            card.MouseLeave += (_, _) => card.Background = Theme.Card;
            card.MouseLeftButtonDown += (_, e) => { e.Handled = true; Navigate(id); };
            sp.Children.Add(card);
        }
        return sp;
    }

    static double? PaceLeft(UsageWindow w, DateTimeOffset now) =>
        w.PaceDelta(now) is { } d ? Math.Clamp(100 - (w.UsedPercent - d), 0, 100) : null;

    static Border PlanChip(string plan) => new()
    {
        BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3),
        VerticalAlignment = VerticalAlignment.Center, Child = Visuals.Label(plan, 12, Theme.Text),
    };

    public static string WindowLabel(string label) => L.Window(label);

    UIElement BuildProvider(ProviderSnapshot p)
    {
        var now = DateTimeOffset.Now;
        var sp = new StackPanel();

        var head = new DockPanel();
        if (p.Plan is { } plan) { var chip = PlanChip(plan); DockPanel.SetDock(chip, Dock.Right); head.Children.Add(chip); }
        var headIcon = Visuals.Icon(p.ProviderKind, 26); headIcon.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(headIcon);
        head.Children.Add(Visuals.Label(p.DisplayName, 22, weight: FontWeights.Bold));
        sp.Children.Add(head);
        if (p.Account != null)
        {
            var acc = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            acc.Children.Add(Visuals.Label(p.Account, 12.5, Theme.SubText));
            if (p.ProviderKind is "antigravity" or "codex" && p.IsActiveAccount)
                acc.Children.Add(new Border
                {
                    Background = Theme.Accent, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0),
                    Child = Visuals.Label(L.T("active", "aktif"), 11, Brushes.White),
                });
            sp.Children.Add(acc);
        }

        var links = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        if (p.StatusUrl != null) links.Children.Add(Visuals.LinkButton(L.T("Status", "Durum"), () => App.OpenUrl(p.StatusUrl!)));
        if (p.DashboardUrl != null) links.Children.Add(Visuals.LinkButton(L.T("Usage dashboard", "Kullanım paneli"), () => App.OpenUrl(p.DashboardUrl!)));
        foreach (var b in links.Children.OfType<Button>()) { b.Background = Theme.Chip; b.BorderBrush = Theme.Border; }
        if (p.ProviderKind == "antigravity" && !p.IsActiveAccount && p.Account != null)
            links.Children.Add(Visuals.Chip(L.T("Make active", "Bu hesaba geç"), () => SwitchAntigravity(p.Account!), primary: true));
        sp.Children.Add(links);
        if (p.ProviderKind == "codex")
            sp.Children.Add(Wrap(Visuals.Chip(L.T("Manage accounts", "Hesapları yönet"), () => Navigate("codex-accounts"))));
        if (p.ProviderKind == "codex" && !p.IsActiveAccount && Guid.TryParse(p.Id.Replace("codex:", ""), out var codexId))
            sp.Children.Add(Wrap(Visuals.Chip(L.T("Switch to this account", "Bu hesaba geç"), () => CodexAccountsPage.ConfirmSwitch(this, codexId), primary: true)));
        if (App.Instance.HealthOf(p.ProviderKind) is { } incident) sp.Children.Add(IncidentLine(incident, compact: false));

        if (p.Status != ProviderStatus.Ok)
        {
            var msg = Visuals.Label(p.Message ?? "", 13, p.Status == ProviderStatus.NeedsLogin ? Theme.Warn : Theme.Bad);
            msg.TextWrapping = TextWrapping.Wrap; msg.Margin = new Thickness(0, 10, 0, 10);
            sp.Children.Add(msg);
            if (p.ProviderKind == "claude" && p.Status == ProviderStatus.NeedsLogin)
                sp.Children.Add(Wrap(Visuals.Chip(L.T("Sign in to claude.ai", "claude.ai'ye giriş yap"), () => ClaudeLoginWindow.Open(), primary: true)));
            if (p.ProviderKind == "codex" && p.Status == ProviderStatus.NeedsLogin)
                sp.Children.Add(Wrap(Visuals.Chip(L.T("Add account", "Hesap ekle"), () => Navigate("codex-accounts"), primary: true)));
        }

        if (p.Status == ProviderStatus.Ok && p.Windows.Count > 0)
        {
            var hero = new System.Windows.Controls.Primitives.UniformGrid { Columns = Math.Min(3, p.Windows.Count), Margin = new Thickness(0, 12, 0, 4) };
            foreach (var w in p.Windows.Take(3))
            {
                var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var pct = Visuals.Label($"{w.LeftPercent:0}%", 19, Theme.Text, FontWeights.Bold); pct.HorizontalAlignment = HorizontalAlignment.Center;
                var lbl = Visuals.Label(L.T("left", "kaldı"), 10.5, Theme.SubText); lbl.HorizontalAlignment = HorizontalAlignment.Center;
                center.Children.Add(pct); center.Children.Add(lbl);
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var ring = Visuals.Ring(w.LeftPercent, 92, 8, p.ProviderKind, center);
                cell.Children.Add(ring);
                var name = Visuals.Label(WindowLabel(w.Label), 12.5, Theme.Text, FontWeights.SemiBold); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 8, 0, 0);
                cell.Children.Add(name);
                if (w.ResetsAt is { } rs)
                {
                    var r2 = Visuals.Label("↻ " + L.Until(rs, now), 11.5, Theme.SubText); r2.HorizontalAlignment = HorizontalAlignment.Center;
                    cell.Children.Add(r2);
                }
                hero.Children.Add(cell);
            }
            sp.Children.Add(hero);
            sp.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 10, 0, 0) });
        }

        if (p.Status == ProviderStatus.Ok && p.Windows.Count > 0)
        {
            var rangeRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var seg = SettingsPage.Segmented(new List<(string, string)> { (L.T("This cycle", "Bu dönem"), "cycle"), (L.T("7 days", "7 gün"), "7d"), (L.T("30 days", "30 gün"), "30d") },
                _chartRange, v => { _chartRange = v; Rebuild(); });
            DockPanel.SetDock(seg, Dock.Right);
            rangeRow.Children.Add(seg);
            rangeRow.Children.Add(new TextBlock { Text = L.T("History", "Geçmiş"), FontSize = 12.5, Foreground = Theme.SubText, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(rangeRow);
        }

        foreach (var w in p.Windows)
        {
            var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(Visuals.Label(WindowLabel(w.Label), 15, weight: FontWeights.SemiBold));
            titleRow.Children.Add(Visuals.Dot(Visuals.WindowStatus(w, now)));
            block.Children.Add(titleRow);
            var bar = Visuals.Bar(w.LeftPercent, PaceLeft(w, now), 8, p.ProviderKind); bar.Margin = new Thickness(0, 7, 0, 6);
            block.Children.Add(bar);
            block.Children.Add(TwoCol($"{w.LeftPercent:0}% " + L.T("left", "kaldı"),
                w.ResetsAt is { } r ? L.T("Resets in ", "Sıfırlanma: ") + L.Until(r, now) : ""));
            var delta = w.PaceDelta(now);
            var runOut = w.ProjectedRunOut(now);
            if (delta is { } d && Math.Abs(d) >= 1 || runOut != null)
            {
                var left = delta is { } dd
                    ? dd > 0 ? $"{dd:0}% " + L.T("ahead of pace", "hızlı tüketim") : $"{-dd:0}% " + L.T("in reserve", "rezervde")
                    : "";
                block.Children.Add(TwoCol(left, runOut is { } ro ? L.T("Runs out in ", "Bitiş: ") + L.Until(ro, now) : "", dim: true));
            }
            if (w.Detail != null)
            {
                var det = Visuals.Label(w.Detail, 11.5, Theme.SubText); det.Opacity = 0.8; det.Margin = new Thickness(0, 2, 0, 0);
                block.Children.Add(det);
            }
            if (p.Status == ProviderStatus.Ok) block.Children.Add(Chart(p, w, now));
            sp.Children.Add(block);
        }

        if (p.Info.Count > 0)
        {
            sp.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 18, 0, 8) });
            foreach (var i in p.Info) sp.Children.Add(TwoCol(i.Label, i.Value, margin: 3));
        }
        if (p.ProviderKind == "claude" && App.Instance.Cost is { Last30: > 0 } cost) sp.Children.Add(CostSection(cost));
        if (p.Message != null && p.Status == ProviderStatus.Ok)
        {
            var m = Visuals.Label(p.Message, 12.5, Theme.Bad); m.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(m);
        }
        return sp;
    }

    static string _chartRange = "cycle";

    /// <summary>"What this would cost on the API": Claude Code logs priced at list rates.</summary>
    static FrameworkElement CostSection(CostSummary c)
    {
        var sp = new StackPanel();
        sp.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 18, 0, 10) });
        sp.Children.Add(Visuals.Label(L.T("API value of your Claude Code use", "Claude Code kullanımının API karşılığı"), 13.5, Theme.Text, FontWeights.SemiBold));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (label, value) in new[] { (L.T("Today", "Bugün"), c.Today), (L.T("7 days", "7 gün"), c.Last7), (L.T("This month", "Bu ay"), c.ThisMonth), (L.T("30 days", "30 gün"), c.Last30) })
        {
            var cell = new StackPanel();
            cell.Children.Add(Visuals.Label(CostScan.Money(value), 15, Theme.Text, FontWeights.Bold));
            cell.Children.Add(Visuals.Label(label, 11, Theme.SubText));
            grid.Children.Add(cell);
        }
        sp.Children.Add(grid);
        // 30 small bars, one per day.
        var max = Math.Max(0.01, c.Daily.Max(d => d.Cost));
        var bars = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Height = 34, Margin = new Thickness(0, 6, 0, 2) };
        foreach (var (day, cost) in c.Daily)
            bars.Children.Add(new Border
            {
                Height = Math.Max(2, 34 * cost / max), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(2), Background = cost > 0 ? Visuals.Tint("claude", 220) : Theme.Track,
                ToolTip = day.ToString("d MMM", System.Globalization.CultureInfo.CurrentUICulture) + ": " + CostScan.Money(cost),
            });
        sp.Children.Add(bars);
        var models = string.Join(" · ", c.ByModelLast30.Where(m => m.Cost >= 0.01).Take(4).Select(m => $"{m.Model} {CostScan.Money(m.Cost)}"));
        if (models.Length > 0)
        {
            var ml = Visuals.Label(L.T("30 days: ", "30 gün: ") + models, 11.5, Theme.SubText);
            ml.TextWrapping = TextWrapping.Wrap; ml.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(ml);
        }
        var note = Visuals.Label(L.F("{0} tokens, {1:N0} requests. Estimated from local logs at API list prices; your subscription does not charge this.",
            "{0} token, {1:N0} istek. Yerel kayıtlardan API liste fiyatıyla tahmin; aboneliğin bunu ücretlendirmez.", CostScan.Tokens(c.TokensLast30), c.RequestsLast30), 11, Theme.SubText);
        note.TextWrapping = TextWrapping.Wrap; note.Opacity = 0.8; note.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(note);
        return sp;
    }

    /// <summary>"⚠ Partial outage: …" from the provider's status page; click opens the page.</summary>
    static FrameworkElement IncidentLine(ServiceHealth h, bool compact)
    {
        var color = h.Health >= Health.Major ? Theme.Bad : Theme.Warn;
        var text = (h.Health >= Health.Major ? L.T("Outage", "Kesinti") : L.T("Degraded", "Aksaklık")) + (h.Description.Length > 0 ? ": " + h.Description : "");
        var t = Visuals.Label("⚠ " + text, compact ? 11.5 : 12.5, color);
        t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
        var b = new Border
        {
            Child = t, Cursor = Cursors.Hand, ToolTip = L.T("Open the status page", "Durum sayfasını aç"),
            Margin = compact ? new Thickness(0, 5, 0, 0) : new Thickness(0, 4, 0, 8),
            Padding = compact ? new Thickness(0) : new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(compact ? 0 : 1), BorderBrush = color,
        };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; App.OpenUrl(h.PageUrl); };
        return b;
    }

    /// <summary>History chart for one window, or a hint while there is not enough data yet.</summary>
    static UIElement Chart(ProviderSnapshot p, UsageWindow w, DateTimeOffset now)
    {
        var cycle = _chartRange == "cycle" && w.WindowSeconds is > 0 && w.ResetsAt != null;
        DateTimeOffset from, to;
        if (cycle) { to = w.ResetsAt!.Value.ToLocalTime(); from = to.AddSeconds(-w.WindowSeconds!.Value); }
        else { to = now; from = now.AddDays(_chartRange == "30d" ? -30 : 7 * -1); }
        var pts = App.Demo ? DemoData.History(p.Id, w, from, to) : History.Load(p.Id, w.Key, from.AddHours(-1));
        if (pts.Count < 2)
        {
            var hint = Visuals.Label(L.T("The chart fills in as readings are collected.", "Grafik, veriler toplandıkça dolar."), 11.5, Theme.SubText);
            hint.Opacity = 0.75; hint.Margin = new Thickness(0, 6, 0, 0);
            return hint;
        }
        return new UsageChart(w, pts, p.ProviderKind, cycle, from, to, now);
    }

    static UIElement Wrap(UIElement e) => new StackPanel { Orientation = Orientation.Horizontal, Children = { e }, Margin = new Thickness(0, 0, 0, 6) };

    static Grid TwoCol(string left, string right, bool dim = false, double margin = 0)
    {
        var g = new Grid { Margin = new Thickness(0, margin, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fg = Theme.SubText;
        var a = Visuals.Label(left, 12.5, fg); if (dim) a.Opacity = 0.85;
        var b = Visuals.Label(right, 12.5, fg); if (dim) b.Opacity = 0.85;
        Grid.SetColumn(b, 1);
        g.Children.Add(a); g.Children.Add(b);
        return g;
    }

    public void SwitchAntigravity(string email)
    {
        try
        {
            AntigravityAccounts.Switch(email);
            _ = App.Instance.RefreshAsync("antigravity");
            Navigate($"antigravity:{email}");
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Windows AI Quota Bar"); }
    }

    public static void RunInTerminal(string command)
    {
        try { Process.Start(new ProcessStartInfo("cmd.exe", $"/k {command}") { UseShellExecute = true }); }
        catch (Exception e) { MessageBox.Show(e.Message, "Windows AI Quota Bar"); }
    }
}
