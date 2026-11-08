using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using QuotaBar.Core.Providers;

namespace QuotaBar.App;

public static class SettingsPage
{
    public const string RepoUrl = "https://github.com/valuex-creative/Windows-AI-Quota-Bar";

    static string? _addKind;          // provider tile currently expanded in "Add a provider"
    static string? _addStatus;        // last test result shown under the form
    static bool _addBusy;
    static bool _showGoKeyForm;

    public static UIElement Build(PanelWindow panel)
    {
        var app = App.Instance;
        var s = app.Settings;
        var sp = new StackPanel();
        sp.Children.Add(Visuals.Label(L.T("Settings", "Ayarlar"), 21, weight: FontWeights.SemiBold));
        var sub = Visuals.Label(L.T("Providers, taskbar and how Windows AI Quota Bar runs", "Sağlayıcılar, görev çubuğu ve Windows AI Quota Bar'ın çalışma şekli"), 12.5, Theme.SubText);
        sub.Margin = new Thickness(0, 2, 0, 6);
        sp.Children.Add(sub);

        // ---- General --------------------------------------------------------------------------------
        var general = Card(sp, "", L.T("General", "Genel"));
        general.Children.Add(Row(L.T("Language", "Dil"), null,
            Dropdown(new List<(string, string)> { (L.T("Windows language", "Windows dili"), "auto") }.Concat(L.Languages.Select(l => (l.Name, l.Code))).ToList(),
                s.Language, v => { app.SetLanguage(v); panel.Navigate("settings"); })));
        general.Children.Add(Divider());
        general.Children.Add(Row(L.T("Refresh every", "Yenileme aralığı"), null,
            Segmented(new[] { 1, 2, 5, 10, 15, 30 }.Select(m => ($"{m}", m.ToString())).ToList(), s.RefreshMinutes.ToString(),
                v => { s.RefreshMinutes = int.Parse(v); app.SaveSettings(); }, L.T(" min", " dk"))));
        general.Children.Add(Divider());
        general.Children.Add(Row(L.T("Start with Windows", "Windows ile başlat"), L.T("Runs quietly in the tray", "Tepside sessizce çalışır"),
            Toggle(s.StartWithWindows, v => { s.StartWithWindows = v; app.SaveSettings(); })));
        general.Children.Add(Divider());
        general.Children.Add(Row(L.T("Show service status", "Hizmet durumunu göster"), L.T("Checks the public status pages of Claude, OpenAI, GitHub and Google", "Claude, OpenAI, GitHub ve Google'ın herkese açık durum sayfalarını kontrol eder"),
            Toggle(s.CheckServiceStatus, v => { s.CheckServiceStatus = v; app.SaveSettings(); })));
        general.Children.Add(Divider());
        general.Children.Add(Row(L.T("Estimate API cost", "API maliyetini hesapla"), L.T("Prices your local Claude Code logs at API rates. Only token counts are read; nothing is sent.", "Yerel Claude Code kayıtlarını API fiyatlarıyla hesaplar. Yalnız token sayıları okunur, hiçbir şey gönderilmez."),
            Toggle(s.ScanLocalCost, v => { s.ScanLocalCost = v; app.SaveSettings(); _ = app.ScanCostAsync(); })));
        general.Children.Add(Divider());
        general.Children.Add(Row(L.T("Shortcut", "Kısayol"), L.T("Opens the panel from anywhere", "Paneli her yerden açar"),
            Segmented(new List<(string, string)> { ("Ctrl+Alt+Q", "Ctrl+Alt+Q"), ("Ctrl+Shift+Q", "Ctrl+Shift+Q"), (L.T("Off", "Kapalı"), "") }, s.Hotkey,
                v => { s.Hotkey = v; app.SaveSettings(); })));
        if (app.HotkeyError is { } hkErr)
        {
            var e = Hint(hkErr); e.Foreground = Theme.Warn; e.Margin = new Thickness(0, 4, 0, 0);
            general.Children.Add(e);
        }

        // ---- Notifications --------------------------------------------------------------------------------
        var al = s.Alerts;
        var nt = Card(sp, "", L.T("Notifications", "Bildirimler"));
        nt.Children.Add(Row(L.T("Usage alerts", "Kullanım uyarıları"), L.T("Windows notifications about your limits", "Limitlerin hakkında Windows bildirimleri"),
            Toggle(al.Enabled, v => { al.Enabled = v; app.SaveSettings(); panel.Navigate("settings"); })));
        if (al.Enabled)
        {
            nt.Children.Add(Divider());
            string Pct(string v) => L.T(v + "%", "%" + v);
            nt.Children.Add(Row(L.T("Alert at", "Uyarı eşiği"), L.T("Share of the limit used", "Limitin kullanılan kısmı"),
                Segmented(new List<(string, string)> { (Pct("50/80/95"), "50,80,95"), (Pct("80/95"), "80,95"), (Pct("90"), "90") }, string.Join(",", al.Thresholds),
                    v => { al.Thresholds = v.Split(',').Select(int.Parse).ToList(); app.SaveSettings(); })));
            nt.Children.Add(Divider());
            nt.Children.Add(Row(L.T("About to run out", "Bitmek üzere"), L.T("At the current pace, within the hour", "Mevcut hızla bir saat içinde"),
                Toggle(al.RunOut, v => { al.RunOut = v; app.SaveSettings(); })));
            nt.Children.Add(Divider());
            nt.Children.Add(Row(L.T("Limit reset", "Limit sıfırlandı"), L.T("When a used limit is full again", "Kullanılmış bir limit yeniden dolunca"),
                Toggle(al.Resets, v => { al.Resets = v; app.SaveSettings(); })));
            nt.Children.Add(Divider());
            nt.Children.Add(Row(L.T("Service incidents", "Hizmet kesintileri"), L.T("Major outages on the providers' status pages", "Sağlayıcıların durum sayfasındaki büyük kesintiler"),
                Toggle(al.Incidents, v => { al.Incidents = v; app.SaveSettings(); })));
            nt.Children.Add(Divider());
            nt.Children.Add(Row(L.T("Unused quota", "Kullanılmayan kota"),
                L.F("{0:0}%+ left {1:0} h before a weekly reset", "Haftalık sıfırlamaya {1:0} sa kala %{0:0}+ boşsa", Alerts.UnusedMinLeft, Alerts.UnusedLead.TotalHours),
                Toggle(al.Unused, v => { al.Unused = v; app.SaveSettings(); })));
            var kinds = app.Report?.Providers.Where(p => p.Status != ProviderStatus.Disabled).Select(p => p.ProviderKind).Distinct().ToList() ?? new();
            if (kinds.Count > 0)
            {
                nt.Children.Add(Divider());
                var t = Visuals.Label(L.T("Notify for", "Bildirim gönderilecekler"), 13); t.Margin = new Thickness(0, 2, 0, 8);
                nt.Children.Add(t);
                var wrap = new WrapPanel();
                foreach (var k in kinds)
                {
                    var on = !al.Muted.Contains(k);
                    var name = app.Report!.Providers.First(p => p.ProviderKind == k).DisplayName.Split(' ')[0];
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    var ic = Visuals.Icon(k, 12); ic.Margin = new Thickness(0, 0, 5, 0); ic.Opacity = on ? 1 : 0.45;
                    content.Children.Add(ic);
                    content.Children.Add(Visuals.Label(name, 11.5, on ? Theme.Text : Theme.SubText, on ? FontWeights.SemiBold : FontWeights.Normal));
                    var chip = new Border
                    {
                        Child = content, Padding = new Thickness(8, 3, 9, 4), Margin = new Thickness(0, 0, 5, 5), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
                        BorderThickness = new Thickness(1), BorderBrush = on ? new SolidColorBrush(Visuals.BrandColor(k)) : Theme.Border,
                        Background = on ? Visuals.Tint(k, (byte)(Theme.Dark ? 36 : 22)) : Brushes.Transparent,
                    };
                    var kind = k;
                    chip.MouseLeftButtonDown += (_, e) =>
                    {
                        e.Handled = true;
                        if (!al.Muted.Remove(kind)) al.Muted.Add(kind);
                        app.SaveSettings();
                        panel.Navigate("settings");
                    };
                    wrap.Children.Add(chip);
                }
                nt.Children.Add(wrap);
            }
            nt.Children.Add(Divider());
            nt.Children.Add(Buttons(Visuals.Chip(L.T("Send a test notification", "Deneme bildirimi gönder"), () => app.TestNotification())));
        }

        // ---- Taskbar -------------------------------------------------------------------------------------
        var tb = Card(sp, "", L.T("Taskbar", "Görev çubuğu"));
        tb.Children.Add(Row(L.T("Show on the taskbar", "Görev çubuğunda göster"), L.T("Next to the clock; drag it to move", "Saatin yanında; sürükleyerek taşı"),
            Toggle(s.ShowTaskbarWidget, v => { s.ShowTaskbarWidget = v; app.SaveSettings(); })));
        tb.Children.Add(Divider());
        tb.Children.Add(Row(L.T("Style", "Görünüm"), null,
            Segmented(new List<(string, string)> { (L.T("Rings", "Halkalar"), "rings"), (L.T("Bars", "Çubuklar"), "bars") }, s.WidgetStyle,
                v => { s.WidgetStyle = v; app.SaveSettings(); })));
        tb.Children.Add(Divider());
        tb.Children.Add(Row(L.T("Colors", "Renkler"), L.T("Low limits always turn amber and red", "Azalan limitler her zaman turuncu ve kırmızı olur"),
            Dropdown(new List<(string, string)>
            {
                (L.T("Provider colors", "Sağlayıcı renkleri"), "brand"), (L.T("Green to red", "Yeşilden kırmızıya"), "status"),
                (L.T("Monochrome", "Tek renk"), "mono"), (L.T("Windows accent", "Windows vurgu rengi"), "accent"),
            }, s.WidgetColor, v => { s.WidgetColor = v; app.SaveSettings(); panel.Navigate("settings"); })));
        tb.Children.Add(Divider());
        tb.Children.Add(Row(L.T("Size", "Boyut"), null,
            Segmented(new List<(string, string)> { (L.T("Compact", "Küçük"), "compact"), (L.T("Normal", "Normal"), "normal"), (L.T("Large", "Büyük"), "large") }, s.WidgetSize,
                v => { s.WidgetSize = v; app.SaveSettings(); })));
        tb.Children.Add(Divider());
        tb.Children.Add(Row(L.T("Pick automatically", "Otomatik seç"), L.T("Show the limits closest to running out", "Bitmeye en yakın limitleri göster"),
            Toggle(s.WidgetAuto, v => { s.WidgetAuto = v; app.SaveSettings(); panel.Navigate("settings"); })));
        if (System.Windows.Forms.Screen.AllScreens.Length > 1 || s.WidgetAllMonitors)
        {
            tb.Children.Add(Divider());
            tb.Children.Add(Row(L.T("All monitors", "Tüm ekranlar"), L.T("Also on the taskbars of other screens", "Diğer ekranların görev çubuğunda da"),
                Toggle(s.WidgetAllMonitors, v => { s.WidgetAllMonitors = v; app.SaveSettings(); })));
        }
        if (!s.WidgetAuto)
        {
            tb.Children.Add(Divider());
            var pickTitle = Visuals.Label(L.T("Show up to two limits", "En fazla iki limit seç"), 13);
            pickTitle.Margin = new Thickness(0, 2, 0, 8);
            tb.Children.Add(pickTitle);
            tb.Children.Add(MetricPicker(panel));
        }

        // ---- Connected accounts -------------------------------------------------------------------------
        var acc = Card(sp, "", L.T("Connected accounts", "Bağlı hesaplar"));
        BuildAccounts(acc, panel);

        // ---- Add a provider -----------------------------------------------------------------------------
        var add = Card(sp, "", L.T("Add a provider", "Sağlayıcı ekle"));
        BuildAddGallery(add, panel);

        // ---- AI assistant ---------------------------------------------------------------------------------
        var ai = Card(sp, "", L.T("Ask your AI assistant", "Yapay zekâ asistanına sor"));
        // Demo mode (README screenshots) shows a generic path instead of where this copy really lives.
        var qb = App.Demo ? @"C:\Users\you\AppData\Local\Programs\QuotaBar\qb.exe" : System.IO.Path.Combine(AppContext.BaseDirectory, "qb.exe");
        var cmd = $"claude mcp add --scope user quotabar -- \"{qb}\" mcp";
        ai.Children.Add(Hint(L.T("Windows AI Quota Bar includes an MCP server. Add it once and Claude Code (or any MCP client) can answer \"how much usage do I have left?\".",
                                 "Windows AI Quota Bar bir MCP sunucusu içerir. Bir kez ekle; Claude Code (veya başka bir MCP istemcisi) \"ne kadar kullanımım kaldı?\" sorusunu yanıtlar.")));
        ai.Children.Add(new Border
        {
            Background = Theme.Sidebar, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 8, 0, 8),
            Child = new TextBlock { Text = cmd, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = Theme.Text },
        });
        ai.Children.Add(Buttons(
            Visuals.Chip(L.T("Copy", "Kopyala"), () => { try { Clipboard.SetText(cmd); } catch { } }),
            Visuals.Chip(L.T("Add to Claude Code", "Claude Code'a ekle"), () => PanelWindow.RunInTerminal(cmd), primary: true)));
        ai.Children.Add(Divider());
        ai.Children.Add(Row(L.T("Let assistants switch Antigravity accounts", "Asistanlar Antigravity hesabını değiştirebilsin"),
            L.T("Off: assistants can only read usage", "Kapalı: asistanlar yalnızca kullanımı okuyabilir"),
            Toggle(s.AllowAssistantAccountSwitch, v => { s.AllowAssistantAccountSwitch = v; app.SaveSettings(); })));

        // ---- Updates ----------------------------------------------------------------------------------------
        var upd = Card(sp, "", L.T("Updates", "Güncellemeler"));
        var current = $"{L.T("Version", "Sürüm")} {Updates.Current}";
        string updSub = app.UpdateStatus
            ?? (app.Update is { } nu ? L.F("Version {0} is available", "{0} sürümü hazır", nu.Version) : L.T("Checks GitHub releases", "GitHub sürümlerini kontrol eder"));
        var updBtn = app.Update != null && !app.UpdateBusy
            ? Visuals.Chip(Updates.CanSelfUpdate ? L.T("Update now", "Şimdi güncelle") : L.T("Download", "İndir"), () => _ = app.InstallUpdateAsync(), primary: true)
            : Visuals.Chip(L.T("Check now", "Şimdi kontrol et"), async () => { await app.CheckUpdatesAsync(true); panel.Navigate("settings"); });
        upd.Children.Add(Row(current, updSub, Buttons(updBtn)));
        upd.Children.Add(Divider());
        upd.Children.Add(Row(L.T("Check automatically", "Otomatik kontrol et"), L.T("At start and every 6 hours", "Açılışta ve 6 saatte bir"),
            Toggle(s.CheckForUpdates, v => { s.CheckForUpdates = v; app.SaveSettings(); })));
        upd.Children.Add(Divider());
        upd.Children.Add(Row(L.T("Install automatically", "Otomatik kur"),
            Updates.CanSelfUpdate || App.Demo ? L.T("Downloads, verifies and installs without asking", "Sormadan indirir, doğrular ve kurar")
                                              : L.T("Only for copies installed with Setup", "Yalnızca Setup ile kurulan kopyalarda"),
            Toggle(s.AutoInstallUpdates, v => { s.AutoInstallUpdates = v; app.SaveSettings(); })));

        // ---- About ------------------------------------------------------------------------------------------
        var about = Card(sp, "", L.T("About", "Hakkında"));
        about.Children.Add(Row("Windows AI Quota Bar", L.T("Open source · MIT license", "Açık kaynak · MIT lisansı"),
            Buttons(Visuals.Chip("GitHub", () => App.OpenUrl(RepoUrl)))));
        about.Children.Add(Divider());
        about.Children.Add(Row(L.T("Back up settings", "Ayarları yedekle"), L.T("API keys are never included", "API anahtarları asla dahil edilmez"),
            Buttons(SmallButton(L.T("Export…", "Dışa aktar…"), ExportSettings), SmallButton(L.T("Import…", "İçe aktar…"), () => ImportSettings(panel)))));
        about.Children.Add(Divider());
        about.Children.Add(Buttons(
            Visuals.Chip(L.T("Open data folder", "Veri klasörünü aç"), () => App.OpenUrl(AppPaths.Data)),
            Visuals.Chip(L.T("Quit Windows AI Quota Bar", "Windows AI Quota Bar'dan çık"), () => App.Instance.Quit())));
        sp.Children.Add(new Border { Height = 8 });
        return sp;
    }

    // =====================================================================================================
    // Accounts
    // =====================================================================================================

    static void BuildAccounts(StackPanel card, PanelWindow panel)
    {
        var app = App.Instance;
        var s = app.Settings;
        var report = app.Report;
        ProviderSnapshot? Snap(string id) => report?.Providers.FirstOrDefault(p => p.Id == id || p.Id.StartsWith(id + ":"));
        bool first = true;
        void Sep() { if (!first) card.Children.Add(Divider()); first = false; }

        // Claude
        Sep();
        var claude = Snap("claude");
        var hasSession = App.Demo || SecretStore.Get(ClaudeProvider.SessionKeySecret) != null;
        card.Children.Add(AccountRow("claude", "Claude", StatusText(claude, hasSession ? null : L.T("Not signed in", "Giriş yapılmadı")), claude,
            hasSession
                ? Buttons(Visuals.Chip(L.T("Sign out", "Çıkış"), () => { ClaudeLoginWindow.SignOut(); _ = app.RefreshAsync("claude"); panel.Navigate("settings"); }))
                : Buttons(Visuals.Chip(L.T("Sign in", "Giriş yap"), () => ClaudeLoginWindow.Open(), primary: true)),
            "claude"));

        // Codex
        Sep();
        var codex = Snap("codex");
        card.Children.Add(AccountRow("codex", "Codex / ChatGPT", StatusText(codex, null), codex,
            Buttons(Visuals.Chip(L.T("Manage accounts", "Hesapları yönet"), () => panel.Navigate("codex-accounts"))),
            "codex"));

        // Antigravity (multiple accounts)
        Sep();
        var accounts = App.Demo ? DemoData.AntigravityAccounts : AntigravityAccounts.List();
        card.Children.Add(AccountRow("antigravity", "Antigravity",
            accounts.Count == 0 ? L.T("Sign in with agy once", "Bir kez agy ile giriş yap") : L.F("{0} account(s)", "{0} hesap", accounts.Count),
            null, Buttons(Visuals.Chip(L.T("Add account", "Hesap ekle"), AddAntigravityAccount)), "antigravity"));
        foreach (var a in accounts)
        {
            var row = new Grid { Margin = new Thickness(40, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (!a.Active) actions.Children.Add(SmallButton(L.T("Switch", "Geç"), () => panel.SwitchAntigravity(a.Email), primary: true));
            actions.Children.Add(SmallButton(L.T("Remove", "Kaldır"), () =>
            {
                if (MessageBox.Show(L.F("Forget {0} in Windows AI Quota Bar?", "{0} Windows AI Quota Bar'dan kaldırılsın mı?", a.Email), "Windows AI Quota Bar", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                AntigravityAccounts.Remove(a.Email);
                _ = app.RefreshAsync("antigravity");
                panel.Navigate("settings");
            }));
            Grid.SetColumn(actions, 2); row.Children.Add(actions);
            var plan = report?.Providers.FirstOrDefault(p => p.Id == "antigravity:" + a.Email)?.Plan?.Replace("Google AI ", "");
            var tags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (plan != null) tags.Children.Add(Tag(plan, Theme.SubText));
            if (a.Active) tags.Children.Add(Tag(L.T("active", "aktif"), Theme.Accent));
            Grid.SetColumn(tags, 1); row.Children.Add(tags);
            var em = Visuals.Label(a.Email, 12.5); em.ToolTip = a.Email;
            row.Children.Add(em);
            card.Children.Add(row);
        }

        // OpenCode Go
        Sep();
        var keys = App.Demo ? DemoData.OpenCodeKeys : OpenCodeGoProvider.DiscoverKeys().Select(k => k.Label).ToList();
        card.Children.Add(AccountRow("opencode-go", "OpenCode Go",
            keys.Count == 0 ? L.T("No key found in OpenCode", "OpenCode'da anahtar bulunamadı") : string.Join(", ", keys),
            Snap("opencode-go"), Buttons(Visuals.Chip(L.T("Add key", "Anahtar ekle"), () => { _showGoKeyForm = !_showGoKeyForm; panel.Navigate("settings"); })), "opencode-go"));
        if (_showGoKeyForm)
            card.Children.Add(KeyForm("opencode-go", "opencode.ai", "https://opencode.ai/auth", null, async (label, key) =>
            {
                var lb = string.IsNullOrWhiteSpace(label) ? "OpenCode Go " + (s.OpenCodeExtraKeyLabels.Count + 2) : label.Trim();
                SecretStore.Set("opencode-go:" + lb, key.Trim());
                if (!s.OpenCodeExtraKeyLabels.Contains(lb)) s.OpenCodeExtraKeyLabels.Add(lb);
                s.Save();
                _showGoKeyForm = false;
                await app.RefreshAsync("opencode-go");
                return null;
            }, panel));

        // API-key accounts
        foreach (var k in s.KeyAccounts.ToList())
        {
            Sep();
            var def = KeyProviders.Find(k.Kind);
            var snap = report?.Providers.FirstOrDefault(p => p.Id == $"{k.Kind}:{k.Id}");
            card.Children.Add(AccountRow(k.Kind, def?.Name ?? k.Kind, StatusText(snap, k.Label), snap,
                Buttons(SmallButton(L.T("Remove", "Kaldır"), () =>
                {
                    if (MessageBox.Show(L.F("Remove {0}?", "{0} kaldırılsın mı?", k.Label), "Windows AI Quota Bar", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    KeyProviders.Remove(s, k.Id);
                    _ = app.RefreshAsync();
                    panel.Navigate("settings");
                })), null));
        }
    }

    static string StatusText(ProviderSnapshot? p, string? fallback)
    {
        if (p is null) return fallback ?? L.T("Waiting for first refresh", "İlk yenileme bekleniyor");
        if (p.Status == ProviderStatus.Ok) return string.Join(" · ", new[] { p.Account, p.Plan }.Where(x => !string.IsNullOrEmpty(x)));
        if (p.Status == ProviderStatus.Disabled) return L.T("Hidden", "Gizli");
        return p.Message ?? p.Status.ToString();
    }

    /// <summary>Icon · name/status · actions · (optional) show/hide toggle.</summary>
    static FrameworkElement AccountRow(string kind, string name, string status, ProviderSnapshot? snap, FrameworkElement? actions, string? toggleKind)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Visuals.Tint(kind, (byte)(Theme.Dark ? 40 : 28)),
            Child = Visuals.Icon(kind, 17), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        ((FrameworkElement)tile.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)tile.Child).VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(tile);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Visuals.Label(name, 13.5, weight: FontWeights.SemiBold));
        if (snap != null && snap.Status != ProviderStatus.Disabled)
            title.Children.Add(Visuals.Dot(snap.Status == ProviderStatus.Ok ? Theme.Good : snap.Status == ProviderStatus.NeedsLogin ? Theme.Warn : Theme.Bad, 7));
        text.Children.Add(title);
        var st = Visuals.Label(status, 11.5, snap?.Status is ProviderStatus.NeedsLogin or ProviderStatus.Error ? Theme.Warn : Theme.SubText);
        st.ToolTip = status;
        text.Children.Add(st);
        Grid.SetColumn(text, 1); g.Children.Add(text);

        if (actions != null) { actions.Margin = new Thickness(8, 0, 0, 0); actions.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(actions, 2); g.Children.Add(actions); }
        if (toggleKind != null)
        {
            var app = App.Instance;
            var t = Toggle(app.Settings.IsEnabled(toggleKind), v => { app.Settings.EnabledProviders[toggleKind] = v; app.SaveSettings(); _ = app.RefreshAsync(); });
            t.Margin = new Thickness(10, 0, 0, 0);
            t.ToolTip = L.T("Show this provider", "Bu sağlayıcıyı göster");
            Grid.SetColumn(t, 3); g.Children.Add(t);
        }
        return g;
    }

    static void AddAntigravityAccount()
    {
        var agy = AntigravityAccounts.AgyPath();
        if (agy is null)
        {
            MessageBox.Show(L.T("The Antigravity CLI (agy) is not installed.", "Antigravity CLI (agy) kurulu değil."), "Windows AI Quota Bar");
            return;
        }
        if (MessageBox.Show(L.T(
                "Windows AI Quota Bar will save the current account, sign agy out and open agy in a terminal.\n\nSign in with the other Google account there. Windows AI Quota Bar picks it up automatically.",
                "Windows AI Quota Bar mevcut hesabı kaydedecek, agy oturumunu kapatıp agy'yi bir terminalde açacak.\n\nOrada diğer Google hesabınla giriş yap. Windows AI Quota Bar yeni hesabı otomatik ekler."),
                "Windows AI Quota Bar", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;

        var previous = AntigravityAccounts.PrepareAddAccount();
        try { Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{agy}\"\"") { UseShellExecute = true }); } catch { }

        var started = DateTime.Now;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            if (AntigravityAccounts.ActiveEmail() != null)
            {
                t.Stop();
                AntigravityAccounts.SyncActive();
                _ = App.Instance.RefreshAsync("antigravity");
            }
            else if (DateTime.Now - started > TimeSpan.FromMinutes(5))
            {
                t.Stop();
                if (previous != null) try { AntigravityAccounts.Switch(previous); } catch { }
            }
        };
        t.Start();
    }

    // =====================================================================================================
    // Add a provider
    // =====================================================================================================

    static void BuildAddGallery(StackPanel card, PanelWindow panel)
    {
        card.Children.Add(Hint(L.T("Paste an API key. It is checked once, then stored encrypted for your Windows user only.",
                                   "API anahtarını yapıştır. Bir kez denenir, sonra yalnızca senin Windows kullanıcın için şifreli saklanır.")));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-4, 8, -4, 0) };
        foreach (var def in KeyProviders.Catalog)
        {
            var selected = _addKind == def.Kind;
            var count = App.Instance.Settings.KeyAccounts.Count(a => a.Kind == def.Kind);
            var tile = new Border
            {
                Margin = new Thickness(4), Padding = new Thickness(10, 9, 10, 9), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                BorderBrush = selected ? Theme.Accent : Theme.Border,
                Background = selected ? Visuals.Tint(def.Kind, (byte)(Theme.Dark ? 30 : 18)) : Theme.Card,
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = Visuals.Icon(def.Kind, 22); ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 1, 0, 0);
            g.Children.Add(ic);
            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(Visuals.Label(def.Name, 12.5, weight: FontWeights.SemiBold));
            text.Children.Add(head);
            var d = Visuals.Label(def.Description, 11, Theme.SubText);
            d.TextWrapping = TextWrapping.Wrap; d.TextTrimming = TextTrimming.None; d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
            if (count > 0 || def.Beta)
            {
                var t = count > 0 ? Tag(L.F("✓ {0} added", "✓ {0} eklendi", count), Theme.Good) : Tag("beta", Theme.SubText);
                t.Margin = new Thickness(0, 5, 0, 0); t.HorizontalAlignment = HorizontalAlignment.Left;
                text.Children.Add(t);
            }
            Grid.SetColumn(text, 1); g.Children.Add(text);
            tile.Child = g;
            var kind = def.Kind;
            tile.MouseEnter += (_, _) => { if (!selected) tile.Background = Theme.Hover; };
            tile.MouseLeave += (_, _) => { if (!selected) tile.Background = Theme.Card; };
            tile.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _addKind = selected ? null : kind;
                _addStatus = null;
                panel.Navigate("settings");
            };
            grid.Children.Add(tile);
        }
        card.Children.Add(grid);

        if (_addKind != null && KeyProviders.Find(_addKind) is { } sel)
        {
            card.Children.Add(KeyForm(sel.Kind, sel.KeyHint, sel.KeyUrl, sel.AutoDetect, async (label, key) =>
            {
                var snap = await KeyProviders.Test(sel.Kind, key, CancellationToken.None);
                if (snap.Status != ProviderStatus.Ok) return snap.Message ?? L.T("Could not read usage with this key.", "Bu anahtarla kullanım okunamadı.");
                KeyProviders.Add(App.Instance.Settings, sel.Kind, label, key);
                _addKind = null;
                _ = App.Instance.RefreshAsync(sel.Kind);
                return null;
            }, panel));
        }

        var more = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 12, Foreground = Theme.SubText, TextWrapping = TextWrapping.Wrap };
        more.Inlines.Add(L.T("Missing one (Cursor, Windsurf, Augment…)? ", "Eksik mi (Cursor, Windsurf, Augment…)? "));
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(L.T("Request it on GitHub", "GitHub'da iste"))) { Foreground = Theme.Accent };
        link.Click += (_, _) => App.OpenUrl(RepoUrl + "/issues/new?title=Provider%20request%3A%20");
        more.Inlines.Add(link);
        card.Children.Add(more);
    }

    /// <summary>Label + key inputs with Test & add. onSave returns an error message or null on success.</summary>
    static FrameworkElement KeyForm(string kind, string keyHint, string? keyUrl, Func<string?>? autoDetect, Func<string, string, Task<string?>> onSave, PanelWindow panel)
    {
        var box = new Border
        {
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12), CornerRadius = new CornerRadius(10),
            Background = Theme.Sidebar, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
        };
        var sp = new StackPanel();
        var name = KeyProviders.Find(kind)?.Name ?? "OpenCode Go";
        sp.Children.Add(Visuals.Label(L.T("Add ", "") + name + L.T("", " ekle"), 13, weight: FontWeights.SemiBold));

        var label = Input(L.T("Name (optional)", "İsim (isteğe bağlı)"));
        var key = new PasswordBox { Padding = new Thickness(8, 6, 8, 6), Background = Theme.Chip, Foreground = Theme.Text, BorderBrush = Theme.Border, CaretBrush = Theme.Text, FontSize = 12.5 };
        sp.Children.Add(FieldLabel(L.T("Name", "İsim")));
        sp.Children.Add(label.host);
        sp.Children.Add(FieldLabel(keyHint.Contains("key", StringComparison.OrdinalIgnoreCase) || keyHint.Contains("token", StringComparison.OrdinalIgnoreCase) ? keyHint : L.T("API key", "API anahtarı") + $"  ({keyHint})"));
        sp.Children.Add(key);

        var status = Visuals.Label(_addStatus ?? "", 12, Theme.Bad);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 6, 0, 0);
        status.Visibility = string.IsNullOrEmpty(_addStatus) ? Visibility.Collapsed : Visibility.Visible;

        Button? save = null;
        save = Visuals.Chip(L.T("Test & add", "Dene ve ekle"), async () =>
        {
            if (_addBusy || key.Password.Trim().Length == 0) return;
            _addBusy = true;
            ((TextBlock)save!.Content).Text = L.T("Checking…", "Deneniyor…");
            string? err;
            try { err = await onSave(label.box.Text, key.Password); }
            catch (Exception e) { err = e.Message; }
            _addBusy = false;
            _addStatus = err;
            panel.Navigate("settings");
        }, primary: true);
        var buttons = Buttons(save);
        if (autoDetect != null)
            buttons.Children.Add(Visuals.Chip(L.T("Use my GitHub CLI login", "GitHub CLI girişimi kullan"), () =>
            {
                var found = autoDetect();
                status.Visibility = Visibility.Visible;
                if (found != null)
                {
                    // Placeholder, not the token: the token is read from `gh` at each refresh and never stored here.
                    key.Password = found;
                    status.Foreground = Theme.SubText;
                    status.Text = L.T("Your GitHub CLI login will be used at each refresh. The token is not copied or stored.",
                                      "Her yenilemede GitHub CLI girişin kullanılır. Token kopyalanmaz, saklanmaz.");
                }
                else status.Text = L.T("Nothing found. Run `gh auth login` first.", "Bulunamadı. Önce `gh auth login` çalıştır.");
            }));
        if (keyUrl != null) buttons.Children.Add(Visuals.Chip(L.T("Get a key ↗", "Anahtar al ↗"), () => App.OpenUrl(keyUrl)));
        buttons.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(buttons);
        sp.Children.Add(status);
        box.Child = sp;
        box.Loaded += (_, _) => box.Dispatcher.BeginInvoke(() => { box.BringIntoView(); key.Focus(); }, DispatcherPriority.Background);
        return box;
    }

    // =====================================================================================================
    // Taskbar metric picker
    // =====================================================================================================

    static FrameworkElement MetricPicker(PanelWindow panel)
    {
        var app = App.Instance;
        var s = app.Settings;
        var host = new StackPanel();
        var all = Metrics.All(app.Report).ToList();
        if (all.Count == 0) { host.Children.Add(Hint(L.T("Waiting for data…", "Veri bekleniyor…"))); return host; }
        foreach (var group in all.GroupBy(m => m.Provider.Id))
        {
            var prov = group.First().Provider;
            var kind = prov.ProviderKind;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
            var ic = Visuals.Icon(kind, 13); ic.Margin = new Thickness(0, 0, 6, 0); who.Children.Add(ic);
            var nm = Visuals.Label(kind is "claude" or "codex" ? prov.DisplayName.Split(' ')[0] : Visuals.ShortName(prov), 12, Theme.SubText);
            nm.MaxWidth = 92; nm.ToolTip = prov.Account ?? prov.DisplayName; who.Children.Add(nm);
            row.Children.Add(who);
            var wrap = new WrapPanel();
            foreach (var m in group)
            {
                var idx = s.WidgetMetrics.IndexOf(m.Id);
                var on = idx >= 0;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                if (on)
                    content.Children.Add(new Border
                    {
                        Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Visuals.BrandColor(kind)), Margin = new Thickness(0, 0, 5, 0),
                        Child = new TextBlock { Text = (idx + 1).ToString(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    });
                content.Children.Add(Visuals.Label(PanelWindow.WindowLabel(m.Window.Label), 11.5, on ? Theme.Text : Theme.SubText, on ? FontWeights.SemiBold : FontWeights.Normal));
                var chip = new Border
                {
                    Child = content, Padding = new Thickness(on ? 5 : 9, 3, 9, 4), Margin = new Thickness(0, 0, 5, 5), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(1),
                    BorderBrush = on ? new SolidColorBrush(Visuals.BrandColor(kind)) : Theme.Border,
                    Background = on ? Visuals.Tint(kind, (byte)(Theme.Dark ? 36 : 22)) : Brushes.Transparent,
                };
                var id = m.Id;
                chip.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    var list = s.WidgetMetrics.Where(x => Metrics.Resolve(app.Report, x) != null).ToList();
                    if (!list.Remove(id)) { list.Add(id); while (list.Count > 2) list.RemoveAt(0); }
                    s.WidgetMetrics = list;
                    app.SaveSettings();
                    panel.Navigate("settings");
                };
                wrap.Children.Add(chip);
            }
            Grid.SetColumn(wrap, 1); row.Children.Add(wrap);
            host.Children.Add(row);
        }
        return host;
    }

    // =====================================================================================================
    // Building blocks
    // =====================================================================================================

    static StackPanel Card(StackPanel parent, string glyph, string title)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 18, 0, 8) };
        head.Children.Add(Visuals.Glyph(glyph, 14, Theme.SubText));
        var t = Visuals.Label(title.ToUpper(System.Globalization.CultureInfo.CurrentUICulture), 11.5, Theme.SubText, FontWeights.SemiBold);
        t.Margin = new Thickness(8, 0, 0, 0);
        head.Children.Add(t);
        parent.Children.Add(head);
        var body = new StackPanel();
        parent.Children.Add(new Border
        {
            Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10), Child = body,
        });
        return body;
    }

    static Border Divider() => new() { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 8, 0, 8), Opacity = 0.7 };

    static Grid Row(string title, string? subtitle, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 32 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Visuals.Label(title, 13));
        if (subtitle != null) text.Children.Add(Visuals.Label(subtitle, 11.5, Theme.SubText));
        g.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    static StackPanel Buttons(params Button[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) sp.Children.Add(b);
        if (sp.Children.Count > 0 && sp.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        return sp;
    }

    static Button SmallButton(string text, Action click, bool primary = false)
    {
        var b = Visuals.Chip(text, click, primary);
        b.Padding = new Thickness(9, 2, 9, 3);
        b.Margin = new Thickness(6, 0, 0, 0);
        ((TextBlock)b.Content).FontSize = 11.5;
        return b;
    }

    static Border Tag(string text, Brush color) => new()
    {
        Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(5, 0, 5, 1), CornerRadius = new CornerRadius(4),
        BorderBrush = color, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
        Child = Visuals.Label(text, 10, color),
    };

    static TextBlock Hint(string text)
    {
        var t = Visuals.Label(text, 12, Theme.SubText);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        return t;
    }

    static TextBlock FieldLabel(string text)
    {
        var t = Visuals.Label(text, 11.5, Theme.SubText);
        t.Margin = new Thickness(0, 10, 0, 4);
        return t;
    }

    static (Grid host, TextBox box) Input(string placeholder)
    {
        var box = new TextBox { Padding = new Thickness(8, 6, 8, 6), Background = Theme.Chip, Foreground = Theme.Text, BorderBrush = Theme.Border, CaretBrush = Theme.Text, FontSize = 12.5 };
        var ph = Visuals.Label(placeholder, 12.5, Theme.SubText);
        ph.Margin = new Thickness(11, 0, 0, 0); ph.IsHitTestVisible = false; ph.Opacity = 0.7;
        box.TextChanged += (_, _) => ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var g = new Grid();
        g.Children.Add(box); g.Children.Add(ph);
        return (g, box);
    }

    /// <summary>iOS/Windows-style switch.</summary>
    public static FrameworkElement Toggle(bool value, Action<bool> changed)
    {
        var on = value;
        var knob = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(on ? 21 : 3, 0, 0, 0) };
        var track = new Border { Width = 40, Height = 20, CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, BorderThickness = new Thickness(1), Child = new Grid { Children = { knob } } };
        void Paint()
        {
            track.Background = on ? Theme.Accent : Brushes.Transparent;
            track.BorderBrush = on ? Theme.Accent : Theme.SubText;
            knob.Fill = on ? Brushes.White : Theme.SubText;
        }
        Paint();
        track.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            Paint();
            knob.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(new Thickness(on ? 21 : 3, 0, 0, 0), TimeSpan.FromMilliseconds(140))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            changed(on);
        };
        return track;
    }

    /// <summary>Pill-shaped segmented control.</summary>
    static void ExportSettings()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "WindowsAIQuotaBar-settings.json", DefaultExt = ".json", Filter = "JSON (*.json)|*.json",
            Title = L.T("Export settings", "Ayarları dışa aktar"),
        };
        if (dlg.ShowDialog() != true) return;
        try { System.IO.File.WriteAllText(dlg.FileName, SettingsTransfer.Export(App.Instance.Settings)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Windows AI Quota Bar"); }
    }

    static void ImportSettings(PanelWindow panel)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { DefaultExt = ".json", Filter = "JSON (*.json)|*.json", Title = L.T("Import settings", "Ayarları içe aktar") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var s = SettingsTransfer.Import(System.IO.File.ReadAllText(dlg.FileName), App.Instance.Settings);
            App.Instance.ApplySettings(s);
            panel.Navigate("settings");
        }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or System.Text.Json.JsonException)
        {
            MessageBox.Show(L.T("This file is not a settings backup.", "Bu dosya bir ayar yedeği değil.") + "\n" + ex.Message, "Windows AI Quota Bar");
        }
    }

    /// <summary>A button showing the current choice; click opens a menu with all options.</summary>
    public static FrameworkElement Dropdown(List<(string text, string value)> options, string current, Action<string> changed)
    {
        var label = options.FirstOrDefault(o => o.value == current).text ?? options[0].text;
        Button? b = null;
        b = SmallButton(label + "  ▾", () =>
        {
            var m = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var (text, value) in options)
            {
                var i = new MenuItem { Header = text, IsChecked = value == current };
                i.Click += (_, _) => changed(value);
                m.Items.Add(i);
            }
            m.IsOpen = true;
        });
        return b;
    }

    public static FrameworkElement Segmented(List<(string text, string value)> options, string current, Action<string> changed, string suffix = "")
    {
        var outer = new Border { CornerRadius = new CornerRadius(9), Background = Theme.Sidebar, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var items = new List<(Border b, TextBlock t, string v)>();
        void Paint(string sel)
        {
            foreach (var (b, t, v) in items)
            {
                var on = v == sel;
                b.Background = on ? Theme.Accent : Brushes.Transparent;
                t.Foreground = on ? Brushes.White : Theme.Text;
                t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        for (int i = 0; i < options.Count; i++)
        {
            var (text, value) = options[i];
            var t = Visuals.Label(text + (i == options.Count - 1 ? suffix : ""), 12);
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 3, 9, 4), Cursor = Cursors.Hand, Child = t, Background = Brushes.Transparent };
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; Paint(value); changed(value); };
            items.Add((b, t, value));
            sp.Children.Add(b);
        }
        Paint(current);
        outer.Child = sp;
        return outer;
    }
}
