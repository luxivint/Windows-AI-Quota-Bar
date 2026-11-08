using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using QuotaBar.App;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/screenshots");
        Directory.CreateDirectory(output);
        var before = HostProcesses();
        var app = new App(false);
        app.InitializeComponent(); Theme.Init(); L.Init("tr");
        var panel = new PanelWindow();
        panel.Navigate("codex-accounts");
        Require(panel.Width == 420 && panel.Height == 660, "Codex must keep the original 420 by 660 panel size.");
        Capture(panel, Path.Combine(output, "codex-accounts.png"));
        Require(Texts(panel).Contains("work@example.com") && Texts(panel).Contains("personal@example.com"), "The page must show both fake accounts.");
        Require(Buttons(panel).Count(b => Caption(b) == "Geç") == 29, "Thirty accounts must be reachable in one compact account list.");
        Require(Descendants((DependencyObject)panel.Content).OfType<FrameworkElement>().Count(e => e.ToolTip?.ToString() == "Codex hesapları") == 1, "The sidebar must contain one Codex item for all thirty accounts.");
        var search = Descendants((DependencyObject)panel.Content).OfType<TextBox>().Single(t => t.Name == "CodexAccountSearch");
        search.Text = "account30";
        Require(Buttons(panel).Count(b => Caption(b) == "Geç") == 1, "Search must filter accounts without leaving the list.");
        search.Text = "";
        Environment.SetEnvironmentVariable("QUOTABAR_DEMO_ACCOUNTS", "3");
        app.RefreshAsync().GetAwaiter().GetResult(); panel.Navigate("codex-accounts");
        Capture(panel, Path.Combine(output, "codex-accounts-compact.png"));
        Environment.SetEnvironmentVariable("QUOTABAR_DEMO_ACCOUNTS", null);
        app.RefreshAsync().GetAwaiter().GetResult(); panel.Navigate("codex-accounts");
        Click(panel, "Hesapları yönet");
        Capture(panel, Path.Combine(output, "codex-manage.png"));
        Require(Buttons(panel).Any(b => Caption(b) == "Hesap ekle"), "Manage must expose browser sign-in.");
        Require(Buttons(panel).Count(b => Caption(b) == "×") == 29, "Only inactive accounts can be removed.");
        Click(panel, "Geç");
        Capture(panel, Path.Combine(output, "codex-switch-confirm.png"));
        Require(Texts(panel).Any(t => t.Contains("kaydedilmemiş")), "Confirmation must explain Desktop interruption.");
        Click(panel, "İptal");
        Require(!Buttons(panel).Any(b => Caption(b) == "Geç ve Codex’i yeniden aç"), "Cancel must leave confirmation without switching.");
        Click(panel, "×");
        Require(Buttons(panel).Any(b => Caption(b) == "Hesabı kaldır"), "Removal requires its own in-panel confirmation.");
        Click(panel, "İptal");
        Require(before.SetEquals(HostProcesses()), "UI checks must never start the account engine.");
        panel.Close(); app.Shutdown();
        Console.WriteLine("PASS: original 420x660 panel, one Codex sidebar item, thirty accounts in one searchable list, protected active row and in-panel confirmations. No host or real account operations.");
        return 0;
    }
    static HashSet<int> HostProcesses()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("SwitcherHost")) { ids.Add(process.Id); process.Dispose(); }
        return ids;
    }
    static void Capture(PanelWindow panel, string path)
    {
        var root = (FrameworkElement)panel.Content;
        var width = (int)panel.Width; var height = (int)panel.Height;
        panel.Content = null;
        using var source = new HwndSource(new HwndSourceParameters("Quota Bar isolated render")
        {
            Width = width, Height = height, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
        });
        source.RootVisual = root; // Hidden presentation source: runs real layout/animations without showing a window.
        root.Width = width; root.Height = height;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var composition = new DrawingVisual();
        using (var drawing = composition.RenderOpen())
        {
            drawing.DrawRectangle(Theme.Bg, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(composition);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
        source.RootVisual = null;
        panel.Content = root;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var pending = new Queue<DependencyObject>(); pending.Enqueue(parent);
        var visited = new HashSet<DependencyObject>();
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            if (current is Visual)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Enqueue(VisualTreeHelper.GetChild(current, i));
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Enqueue(child);
        }
    }
    static IEnumerable<Button> Buttons(PanelWindow panel) => Descendants((DependencyObject)panel.Content).OfType<Button>();
    static string Caption(Button button) => button.Content is TextBlock text ? text.Text : button.Content?.ToString() ?? "";
    static IEnumerable<string> Texts(PanelWindow panel) => Descendants((DependencyObject)panel.Content).OfType<TextBlock>().Select(t => t.Text);
    static void Click(PanelWindow panel, string caption)
    {
        var button = Buttons(panel).First(b => Caption(b) == caption);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var root = (FrameworkElement)panel.Content; root.Measure(new Size(panel.Width, panel.Height)); root.Arrange(new Rect(0, 0, panel.Width, panel.Height)); root.UpdateLayout();
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
