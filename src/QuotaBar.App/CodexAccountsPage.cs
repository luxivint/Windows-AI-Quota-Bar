using System.Windows;
using System.Windows.Controls;
using CodexAccountSwitcher.Core;

namespace QuotaBar.App;

/// <summary>One searchable account/usage table over the original SwitcherCore.</summary>
public static class CodexAccountsPage
{
    static bool _manage, _removing;
    static Guid? _target;
    static string? _error;
    static string _query = "", _sort = "recommended";
    static TextBox? _search;
    static double _scrollOffset;
    static AccountSnapshot State => App.Demo ? DemoState() : App.Instance.Codex.State;
    static bool Busy => !App.Demo && App.Instance.Codex.IsBusy;

    public static void ConfirmSwitch(PanelWindow panel, Guid id)
    {
        _target = id; _removing = false; panel.Navigate("codex-accounts");
    }
    public static void RestoreSearchFocus(int caret)
    {
        if (_search != null) { _search.Focus(); _search.CaretIndex = Math.Min(caret, _search.Text.Length); }
    }

    public static UIElement Build(PanelWindow panel)
    {
        var state = State;
        if (!App.Demo)
        {
            var fetched = App.Instance.Codex.Providers.ToDictionary(p => p.Id, p => p.FetchedAt);
            state = state with { Accounts = state.Accounts.Select(a =>
                fetched.TryGetValue(CodexSnapshotMapper.ProviderId(a.Profile.Id), out var at) && DateTimeOffset.Now - at > TimeSpan.FromMinutes(15)
                    ? a with { UsageStatus = "stale" } : a).ToArray() };
        }
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new StackPanel(); root.Children.Add(header);
        var title = new DockPanel();
        var icon = Visuals.Icon("codex", 26); icon.Margin = new Thickness(0, 0, 10, 0); title.Children.Add(icon);
        title.Children.Add(Visuals.Label("Codex", 22, weight: FontWeights.Bold)); header.Children.Add(title);
        var active = state.ActiveIdentityConfirmed ? state.Accounts.FirstOrDefault(a => a.Profile.Id == state.ActiveAccountID) : null;
        var identity = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        if (active != null)
        {
            var tag = Badge(L.T("active", "aktif"), true); DockPanel.SetDock(tag, Dock.Right); identity.Children.Add(tag);
        }
        var email = Visuals.Label(active?.Profile.Email ?? active?.Profile.DisplayName ?? L.T("Codex accounts", "Codex hesapları"), 12.5, Theme.SubText);
        email.ToolTip = email.Text; identity.Children.Add(email); header.Children.Add(identity);
        if (_target is { } id && state.Accounts.FirstOrDefault(a => a.Profile.Id == id) is { } selected)
        {
            var confirmation = Confirmation(panel, selected); confirmation.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(confirmation, 1); root.Children.Add(confirmation); return root;
        }
        _target = null;
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var add = Visuals.Chip(L.T("Add account", "Hesap ekle"), () => _ = Run(panel, "add")); add.IsEnabled = !Busy;
        actions.Children.Add(add);
        actions.Children.Add(Visuals.Chip(L.T("Manage accounts", "Hesapları yönet"), () => { _manage = !_manage; panel.Navigate("codex-accounts"); }));
        header.Children.Add(actions);
        if (active?.Usage is { } usage)
        {
            var rings = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 10) };
            rings.Children.Add(Hero(L.T("5 hours", "5 saat"), usage.FiveHourRemainingPercent, usage.FiveHourResetsAt));
            rings.Children.Add(Hero(L.T("Weekly", "Haftalık"), usage.RemainingPercent, usage.ResetsAt));
            header.Children.Add(rings);
        }
        var error = _error ?? (App.Demo ? null : App.Instance.Codex.Error);
        if (error != null)
        {
            header.Children.Add(Text(error, Theme.Warn));
            header.Children.Add(Visuals.Chip(L.T("Dismiss", "Kapat"), () => { _error = null; _ = Run(panel, "dismissError"); }));
        }
        if (_manage || state.Accounts.Length == 0)
        {
            var register = Visuals.Chip(L.T("Register current account", "Mevcut hesabı ekle"), () => _ = Run(panel, "register"));
            register.IsEnabled = !Busy; register.Margin = new Thickness(0, 8, 0, 0); header.Children.Add(register);
            var resets = new CheckBox { Content = new TextBlock { Text = L.T("Use resets before expiry", "Süresi dolmadan sıfırlama haklarını kullan"), TextWrapping = TextWrapping.Wrap },
                Foreground = Theme.SubText, IsChecked = state.Settings.AutoResetEnabled, IsEnabled = !Busy, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
            resets.ToolTip = L.T("When enabled, uses expiring reset credits for all saved accounts. Keep Quota Bar running. This does not switch accounts.",
                "Açıksa tüm kayıtlı hesapların süresi dolmak üzereki sıfırlama haklarını kullanır. Quota Bar açık kalmalıdır. Hesap değiştirmez.");
            resets.Checked += (_, _) => _ = Run(panel, "autoReset", value: true);
            resets.Unchecked += (_, _) => _ = Run(panel, "autoReset", value: false); header.Children.Add(resets);
        }
        if (state.IsAddingAccount)
        {
            header.Children.Add(Text(L.T("Complete sign-in in your browser. Your active Codex account stays unchanged.", "Tarayıcıdaki girişi tamamla. Codex’teki aktif hesabın değişmez."), Theme.SubText));
            header.Children.Add(Visuals.Chip(L.T("Cancel sign-in", "Girişi iptal et"), () => _ = Run(panel, "cancelAdd")));
        }
        if (state.IsMutating) header.Children.Add(Text(L.T("Completing the account operation…", "Hesap işlemi tamamlanıyor…"), Theme.SubText));
        header.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 4, 0, 12) });
        var label = new DockPanel();
        var count = Visuals.Label(L.F("{0} account(s)", "{0} hesap", state.Accounts.Length), 11.5, Theme.SubText);
        DockPanel.SetDock(count, Dock.Right); label.Children.Add(count);
        label.Children.Add(Visuals.Label(L.T("Accounts", "Hesaplar"), 15, weight: FontWeights.SemiBold)); header.Children.Add(label);
        var best = CodexAccountRanking.Recommend(state.Accounts);
        var filters = new Grid { Margin = new Thickness(0, 9, 0, 8) };
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox { Name = "CodexAccountSearch", Text = _query, FontSize = 11.5, Padding = new Thickness(8, 5, 8, 5),
            Background = Theme.Chip, Foreground = Theme.Text, BorderBrush = Theme.Border, BorderThickness = new Thickness(0), CaretBrush = Theme.Text };
        _search = search; System.Windows.Automation.AutomationProperties.SetName(search, L.T("Search accounts", "Hesap ara"));
        var searchHost = new Grid { Margin = new Thickness(0, 0, 8, 0) }; searchHost.Children.Add(search);
        var placeholder = Visuals.Label(L.T("Search accounts…", "Hesap ara…"), 11.5, Theme.SubText);
        placeholder.Margin = new Thickness(8, 0, 0, 0); placeholder.IsHitTestVisible = false;
        placeholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; searchHost.Children.Add(placeholder);
        var roundedSearch = new Border { Child = searchHost, CornerRadius = new CornerRadius(6), Background = Theme.Chip, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), ClipToBounds = true };
        filters.Children.Add(roundedSearch);
        var sort = SettingsPage.Dropdown(new List<(string, string)> {
            (L.T("Recommended", "Önerilen"), "recommended"), (L.T("Weekly", "Haftalık"), "weekly"),
            (L.T("5 hours", "5 saat"), "session"), (L.T("Next reset", "Yakın yenilenmeye göre"), "reset") }, _sort,
            value => { _sort = value; _scrollOffset = 0; panel.Navigate("codex-accounts"); });
        sort.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(sort, 1); filters.Children.Add(sort); header.Children.Add(filters);
        var rows = new StackPanel();
        var scroll = new ScrollViewer { Content = rows, Name = "CodexAccountList", VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        void RenderRows()
        {
            rows.Children.Clear();
            foreach (var row in CodexAccountRanking.Order(state.Accounts, _query, _sort)) rows.Children.Add(AccountRow(panel, state, row, row.Profile.Id == best));
            if (rows.Children.Count == 0) rows.Children.Add(Text(L.T("No matching accounts.", "Eşleşen hesap yok."), Theme.SubText));
        }
        RenderRows();
        var savedOffset = _scrollOffset;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(savedOffset);
        scroll.ScrollChanged += (_, _) => { if (scroll.IsLoaded) _scrollOffset = scroll.VerticalOffset; };
        search.TextChanged += (_, _) => { _query = search.Text; placeholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _scrollOffset = 0; RenderRows(); scroll.ScrollToTop(); };
        return root;
    }

    static Border Badge(string label, bool active) => new() {
        Child = Visuals.Label(label, 10.5, active ? System.Windows.Media.Brushes.White : Theme.SubText),
        Background = active ? Theme.Accent : Theme.Chip, CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    static FrameworkElement Hero(string title, int? remaining, DateTimeOffset? reset)
    {
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var percent = Visuals.Label(remaining is { } n ? $"{n}%" : "—", 19, Theme.Text, FontWeights.Bold); percent.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(percent); var left = Visuals.Label(L.T("left", "kaldı"), 10.5, Theme.SubText); left.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(left);
        var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        cell.Children.Add(Visuals.Ring(remaining ?? 0, 92, 8, "codex", center));
        var name = Visuals.Label(title, 12.5, Theme.Text, FontWeights.SemiBold); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 7, 0, 0); cell.Children.Add(name);
        var time = Visuals.Label(reset is { } date ? "↻ " + L.Until(date, DateTimeOffset.Now) : "—", 11, Theme.SubText); time.HorizontalAlignment = HorizontalAlignment.Center; cell.Children.Add(time);
        return cell;
    }

    static Border AccountRow(PanelWindow panel, AccountSnapshot state, AccountRow row, bool recommended)
    {
        var active = state.ActiveIdentityConfirmed && row.Profile.Id == state.ActiveAccountID;
        var body = new StackPanel();
        var heading = new DockPanel();
        if (!active)
        {
            var change = Visuals.Chip(L.T("Switch", "Geç"), () => ConfirmSwitch(panel, row.Profile.Id));
            change.IsEnabled = !Busy; change.FontSize = 11; change.Padding = new Thickness(8, 2, 8, 3); change.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(change, Dock.Right); heading.Children.Add(change);
        }
        if (_manage && !active)
        {
            var remove = Visuals.Chip("×", () => { _target = row.Profile.Id; _removing = true; panel.Navigate("codex-accounts"); });
            remove.ToolTip = L.T("Remove", "Kaldır"); remove.IsEnabled = !Busy; remove.Padding = new Thickness(6, 2, 6, 3); remove.Margin = new Thickness(5, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right); heading.Children.Add(remove);
        }
        var name = Visuals.Label(row.Profile.Email ?? row.Profile.DisplayName, 12.5, Theme.Text, FontWeights.SemiBold); name.ToolTip = name.Text;
        heading.Children.Add(name); body.Children.Add(heading);
        var statusLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 5) };
        if (active || recommended) { var badge = Badge(active ? L.T("active", "aktif") : L.T("Recommended", "Önerilen"), active); badge.Margin = new Thickness(0); statusLine.Children.Add(badge); }
        if (row.UsageStatus == "stale" || row.Usage == null)
            statusLine.Children.Add(Visuals.Label(row.Usage == null ? L.T("Unavailable", "Alınamadı") : L.T("Cached", "Önbellek"), 10.5, Theme.SubText));
        if (statusLine.Children.Count > 0) body.Children.Add(statusLine);
        var quotas = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        quotas.ColumnDefinitions.Add(new ColumnDefinition()); quotas.ColumnDefinitions.Add(new ColumnDefinition());
        var session = Quota(L.T("5 hours", "5 saat"), row.Usage?.FiveHourRemainingPercent, row.Usage?.FiveHourResetsAt); session.Margin = new Thickness(0, 0, 12, 0); quotas.Children.Add(session);
        var weekly = Quota(L.T("Weekly", "Haftalık"), row.Usage?.RemainingPercent, row.Usage?.ResetsAt); Grid.SetColumn(weekly, 1); quotas.Children.Add(weekly); body.Children.Add(quotas);
        var block = new Border { Child = body, BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 9, 0, 11) };
        block.ToolTip = row.UsageError ?? (row.Usage?.ResetCreditsRemaining is { } count ? L.F("Available resets: {0}", "Sıfırlama hakkı: {0}", count) : null);
        return block;
    }

    static FrameworkElement Quota(string title, int? remaining, DateTimeOffset? reset)
    {
        var cell = new StackPanel();
        var line = new DockPanel();
        var number = Visuals.Label(remaining is { } n ? $"{n}%" : "—", 11.5, Theme.Text, FontWeights.SemiBold); DockPanel.SetDock(number, Dock.Right); line.Children.Add(number);
        line.Children.Add(Visuals.Label(title, 11.5, Theme.SubText)); cell.Children.Add(line);
        var bar = Visuals.Bar(remaining ?? 0, null, 5, "codex"); bar.Margin = new Thickness(0, 5, 0, 4); cell.Children.Add(bar);
        var time = Visuals.Label(reset is { } date ? "↻ " + L.Until(date, DateTimeOffset.Now) : "—", 10.5, Theme.SubText); time.ToolTip = reset?.ToLocalTime().ToString("dd MMM yyyy HH:mm"); cell.Children.Add(time);
        return cell;
    }

    static Border Confirmation(PanelWindow panel, AccountRow row)
    {
        var body = new StackPanel();
        body.Children.Add(Visuals.Label(row.Profile.Email ?? row.Profile.DisplayName, 15, weight: FontWeights.SemiBold));
        body.Children.Add(Text(_removing
            ? L.T("Remove this account from the saved list? Its local profile will be deleted.", "Bu hesap kayıtlı listeden kaldırılsın mı? Yerel profili silinecek.")
            : L.T("Codex Desktop will close and reopen with this account. Running Desktop tasks will be interrupted and unsaved work may be lost. Independently opened terminal sessions keep their current account.",
                "Codex Desktop bu hesapla kapanıp yeniden açılacak. Desktop’taki çalışan işler kesilir; kaydedilmemiş çalışmalar kaybolabilir. Bağımsız terminal oturumları mevcut hesaplarıyla devam eder."), Theme.SubText));
        if (_error != null) body.Children.Add(Text(_error, Theme.Bad));
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), IsEnabled = !Busy };
        actions.Children.Add(Visuals.Chip(L.T("Cancel", "İptal"), () => { _target = null; _error = null; panel.Navigate("codex-accounts"); }));
        actions.Children.Add(Visuals.Chip(_removing ? L.T("Remove account", "Hesabı kaldır") : L.T("Switch and reopen Codex", "Geç ve Codex’i yeniden aç"), () =>
        {
            if (App.Demo) { _target = null; panel.Navigate("codex-accounts"); return; }
            var command = _removing ? "remove" : "switch"; _target = null;
            _ = Run(panel, command, row.Profile.Id);
        }, primary: true));
        body.Children.Add(actions);
        return new Border { Child = body, Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16) };
    }
    static TextBlock Text(string text, Brush brush) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = brush, Margin = new Thickness(0, 5, 0, 0) };
    static async Task Run(PanelWindow panel, string command, Guid? id = null, bool? value = null)
    {
        if (App.Demo) return;
        _error = null;
        try { await App.Instance.Codex.CommandAsync(command, id, value); } catch (Exception e) { _error = e.Message; }
        panel.Navigate("codex-accounts");
    }

    internal static AccountSnapshot DemoState()
    {
        var work = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var now = DateTimeOffset.Now;
        var rows = new List<AccountRow> {
            new(new Profile(work, "Work", "work@example.com"), "W", new Usage(72, now.AddDays(3), 48, now.AddHours(2), 2), null,
                Subscription: new SubscriptionSnapshot(now.AddDays(20), now)),
            new(new Profile(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Personal", "personal@example.com"), "P", new Usage(93, now.AddDays(5), 89, now.AddHours(4), 1), null),
        };
        var demoCount = int.TryParse(Environment.GetEnvironmentVariable("QUOTABAR_DEMO_ACCOUNTS"), out var count) ? Math.Clamp(count, 2, 30) : 30;
        for (var i = 3; i <= demoCount; i++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}");
            var usage = i % 13 == 0 ? null : new Usage(i == 7 ? 0 : i * 11 % 101, now.AddHours(i % 7 * 24 + 3), i * 17 % 101, now.AddMinutes(i % 5 * 60 + 15), i % 4);
            var stale = i % 11 == 0;
            rows.Add(new(new Profile(id, $"Account {i:00}", $"account{i:00}@example.com"), "A", usage,
                stale ? L.T("Refresh failed; showing the last reading.", "Yenileme başarısız; son okuma gösteriliyor.") : null,
                usage == null ? "unavailable" : stale ? "stale" : "loaded"));
        }
        return new AccountSnapshot(rows.ToArray(), work, new Preferences(), false, false, true, null, new());
    }
}
