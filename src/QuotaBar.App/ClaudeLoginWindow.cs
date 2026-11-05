using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using QuotaBar.Core.Providers;

namespace QuotaBar.App;

/// <summary>
/// Embedded claude.ai sign-in. When the sessionKey cookie appears, QuotaBar stores it
/// (DPAPI-encrypted, current Windows user only) and closes the window.
/// </summary>
public sealed class ClaudeLoginWindow : Window
{
    static ClaudeLoginWindow? _open;
    readonly WebView2 _web = new();
    readonly TextBlock _status;
    bool _done, _checking;

    public static void Open()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new ClaudeLoginWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public static void SignOut()
    {
        SecretStore.Set(ClaudeProvider.SessionKeySecret, null);
        SecretStore.Set("claude:email", null);
        ClaudeWebBridge.Reset();
        // Clear the claude.ai cookies in the shared browser profile.
        _ = Task.Run(async () =>
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var env = await ClaudeWebBridge.Environment();
                    var host = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                    var c = await env.CreateCoreWebView2ControllerAsync(new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle());
                    c.CoreWebView2.CookieManager.DeleteAllCookies();
                    c.Close();
                    host.Close();
                }).Task.Unwrap();
            }
            catch { }
        });
    }

    ClaudeLoginWindow()
    {
        Title = L.T("Sign in to Claude — Windows AI Quota Bar", "Claude'a giriş — Windows AI Quota Bar");
        Width = 520; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        _status = new TextBlock
        {
            Text = L.T("Sign in with your email (the code option works best here). This window closes by itself.",
                       "E-postanla giriş yap (burada e-posta kodu en sorunsuz yöntem). Pencere kendiliğinden kapanır."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8), FontSize = 12.5,
        };
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;
        Loaded += async (_, _) => await Init();
    }

    async Task Init()
    {
        try
        {
            var env = await ClaudeWebBridge.Environment();
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckCookie();
            _web.CoreWebView2.SourceChanged += async (_, _) => await CheckCookie();
            _web.CoreWebView2.Navigate("https://claude.ai/login");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async (_, _) => { if (_done) timer.Stop(); else await CheckCookie(); };
            timer.Start();
        }
        catch (Exception e)
        {
            _status.Text = L.T("WebView2 runtime is missing: ", "WebView2 çalışma zamanı eksik: ") + e.Message;
        }
    }

    async Task CheckCookie()
    {
        if (_done || _checking || _web.CoreWebView2 is null) return;
        _checking = true; // timer + navigation events can overlap
        try
        {
            var url = _web.Source?.ToString() ?? "";
            // Only accept once the user is past the login pages.
            if (!url.StartsWith("https://claude.ai/") || url.Contains("/login") || url.Contains("/magic-link") || url.Contains("/onboarding")) return;
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://claude.ai");
            var session = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
            if (string.IsNullOrEmpty(session) || _done) return;
            _done = true;
            SecretStore.Set(ClaudeProvider.SessionKeySecret, session);
            App.Log("Claude login: session captured at " + url);
        }
        catch (Exception e) { App.Log("Claude login check: " + e.Message); return; }
        finally { _checking = false; }

        // Close first (outside any WebView2 callback), then refresh in the background.
        _ = Dispatcher.BeginInvoke(() =>
        {
            Hide();
            try { _web.Dispose(); } catch { }
            Close();
            ClaudeWebBridge.Reset();
            _ = App.Instance.RefreshAsync("claude");
            App.Instance.ShowPanel("claude");
        });
    }
}
