using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace QuotaBar.App;

public partial class App : Application
{
    public static App Instance => (App)Current;
    /// <summary>--demo: fake accounts and usage, nothing read from or written to this PC's accounts.</summary>
    public static bool Demo { get; private set; }

    public AppSettings Settings { get; private set; } = new();
    public UsageReport? Report { get; private set; }
    public DateTimeOffset? LastRefresh { get; private set; }
    public DateTimeOffset NextRefresh { get; private set; } = DateTimeOffset.Now;
    public bool Refreshing { get; private set; }
    public event Action? ReportChanged;
    public event Action? SettingsChanged;

    readonly UsageService _service = new();
    TrayIcon? _tray;
    PanelWindow? _panel;
    TaskbarWidget? _widget;
    DispatcherTimer? _timer;
    Mutex? _mutex;
    string? _pending;
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Demo = e.Args.Contains("--demo");
        // --lang=en / --lang=tr overrides the Windows UI language (must run before L is first used).
        if (e.Args.FirstOrDefault(x => x.StartsWith("--lang=")) is { } lang)
        {
            var c = System.Globalization.CultureInfo.GetCultureInfo(lang[7..] == "tr" ? "tr-TR" : "en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = c;
            System.Globalization.CultureInfo.CurrentUICulture = c;
        }
        _mutex = new Mutex(true, Demo ? "QuotaBar.Demo" : "QuotaBar.SingleInstance", out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("QuotaBar.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Demo ? "QuotaBar.DemoShow" : "QuotaBar.Show");
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.Invoke(() => ShowPanel()); }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception.ToString());
            ex.Handled = true;
        };

        Theme.Init();
        ClaudeWebBridge.Register();
        Settings = Demo ? DemoData.Settings() : AppSettings.Load();
        if (!Demo) StartupRegistration.Apply(Settings.StartWithWindows);

        Report = Demo ? DemoData.Report() : UsageService.LoadCached();
        LastRefresh = Report?.GeneratedAt;

        _panel = new PanelWindow();
        _tray = new TrayIcon();
        ApplyWidgetSetting();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => { if (DateTimeOffset.Now >= NextRefresh) _ = RefreshAsync(); };
        _timer.Start();
        SystemEvents.PowerModeChanged += (_, pe) => { if (pe.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(async () => { await Task.Delay(5000); await RefreshAsync(); }); };

        ReportChanged?.Invoke();
        _ = RefreshAsync();
        if (e.Args.Contains("--claude-login")) ClaudeLoginWindow.Open();
        else if (e.Args.Contains("--settings")) ShowPanel("settings");
        else if (!e.Args.Contains("--background")) ShowPanel();
    }

    public async Task RefreshAsync(string? onlyKind = null)
    {
        if (Demo) { Report = DemoData.Report(); LastRefresh = DateTimeOffset.Now; NextRefresh = LastRefresh.Value.AddMinutes(Settings.RefreshMinutes); ReportChanged?.Invoke(); return; }
        if (Refreshing) { _pending = _pending == null || onlyKind == null ? onlyKind ?? "*" : _pending == onlyKind ? onlyKind : "*"; return; }
        Refreshing = true;
        ReportChanged?.Invoke();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Report = await Task.Run(() => _service.RefreshAsync(Settings, onlyKind));
            Log($"refresh {(onlyKind ?? "all")} took {sw.ElapsedMilliseconds} ms");
            LastRefresh = DateTimeOffset.Now;
        }
        catch (Exception ex) { Log(ex.ToString()); }
        finally
        {
            Refreshing = false;
            NextRefresh = DateTimeOffset.Now.AddMinutes(Math.Max(1, Settings.RefreshMinutes));
            ReportChanged?.Invoke();
        }
        if (_pending is { } next)
        {
            _pending = null;
            await RefreshAsync(next == "*" ? null : next);
        }
    }

    public void SaveSettings()
    {
        Settings.Save();
        if (!Demo) StartupRegistration.Apply(Settings.StartWithWindows);
        ApplyWidgetSetting();
        NextRefresh = (LastRefresh ?? DateTimeOffset.Now).AddMinutes(Math.Max(1, Settings.RefreshMinutes));
        SettingsChanged?.Invoke();
        ReportChanged?.Invoke();
    }

    void ApplyWidgetSetting()
    {
        if (Settings.ShowTaskbarWidget && _widget == null) { _widget = new TaskbarWidget(); _widget.Show(); }
        else if (!Settings.ShowTaskbarWidget && _widget != null) { _widget.Close(); _widget = null; }
    }

    public void TogglePanel()
    {
        if (_panel is null) return;
        if (_panel.IsVisible && !_panel.JustHidden) _panel.Hide();
        else ShowPanel();
    }

    public void ShowPanel(string? page = null)
    {
        if (_panel is null) return;
        if (page != null) _panel.Navigate(page);
        _panel.ShowNearTray();
    }

    public void Quit()
    {
        _tray?.Dispose();
        _widget?.Close();
        Shutdown();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.Data, "quotabar.log"), $"[{DateTime.Now:O}] {text}\n"); } catch { }
    }
}

public static class StartupRegistration
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            if (k is null) return;
            var exe = Environment.ProcessPath;
            if (enabled && exe != null) k.SetValue("QuotaBar", $"\"{exe}\" --background");
            else if (!enabled) k.DeleteValue("QuotaBar", false);
        }
        catch { }
    }
}
