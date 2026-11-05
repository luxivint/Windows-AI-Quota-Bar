using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using QuotaBar.Core.Providers;

namespace QuotaBar.App;

/// <summary>
/// A hidden WebView2 that shares the profile of the claude.ai login window. API calls are made with
/// fetch() inside it, so they carry the real browser fingerprint and cookies (Cloudflare lets them through).
/// </summary>
public static class ClaudeWebBridge
{
    static CoreWebView2Environment? _env;
    static CoreWebView2Controller? _controller;
    static Window? _host;
    static Task? _ready;
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Register() => ClaudeProvider.WebFetcher = Fetch;

    /// <summary>Shared environment so the login window and the bridge use one browser process.</summary>
    public static async Task<CoreWebView2Environment> Environment()
    {
        _env ??= await CoreWebView2Environment.CreateAsync(null, AppPaths.WebProfile);
        return _env;
    }

    /// <summary>Drop the hidden view (after sign-out or a new sign-in) so the next call starts clean.</summary>
    public static void Reset()
    {
        // Never close the view under an in-flight fetch; the next call re-initialises (under the gate).
        Application.Current.Dispatcher.Invoke(() => _ready = null);
    }

    static Task<(int, string)> Fetch(string url, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() => FetchOnUi(url, ct)).Task.Unwrap();

    static async Task<(int, string)> FetchOnUi(string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            _ready ??= Init();
            await _ready.WaitAsync(TimeSpan.FromSeconds(40), ct);
            var expr = "fetch(" + JsonSerializer.Serialize(url) + ", {credentials:'include', headers:{'accept':'application/json'}})" +
                       ".then(r => r.text().then(t => JSON.stringify({s: r.status, b: t})))" +
                       ".catch(e => JSON.stringify({s: 0, b: String(e)}))";
            var args = new JsonObject { ["expression"] = expr, ["awaitPromise"] = true, ["returnByValue"] = true }.ToJsonString();
            var raw = await _controller!.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args).WaitAsync(TimeSpan.FromSeconds(30), ct);
            var value = JsonNode.Parse(raw)?["result"]?["value"]?.GetValue<string>() ?? "{\"s\":0,\"b\":\"no result\"}";
            var res = JsonNode.Parse(value)!;
            var status = res["s"]!.GetValue<int>();
            var body = res["b"]!.GetValue<string>();
            if (status == 403 && body.Contains("Just a moment")) _ready = null; // challenge page: reload next time
            return (status, body);
        }
        catch (TimeoutException) { _ready = null; return (0, "claude.ai did not respond"); }
        finally { Gate.Release(); }
    }

    static async Task Init()
    {
        if (_host == null)
        {
            _host = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false };
            new WindowInteropHelper(_host).EnsureHandle(); // never shown
        }
        _controller?.Close();
        var env = await Environment();
        _controller = await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(_host).Handle);
        _controller.IsVisible = false;
        var tcs = new TaskCompletionSource();
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();
        _controller.CoreWebView2.NavigationCompleted += Done;
        // A light same-origin page so fetch() runs on claude.ai with its cookies.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _controller.CoreWebView2.Navigate("https://claude.ai/robots.txt");
        try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { App.Log("ClaudeWebBridge: navigation timeout"); }
        App.Log($"ClaudeWebBridge ready in {sw.ElapsedMilliseconds} ms");
        _controller.CoreWebView2.NavigationCompleted -= Done;
    }
}
