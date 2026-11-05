using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuotaBar.App;

/// <summary>Light/dark palette that follows the Windows app theme.</summary>
public static class Theme
{
    public static bool Dark { get; private set; }
    public static bool TaskbarDark { get; private set; }

    public static Brush Bg = null!, Sidebar = null!, Text = null!, SubText = null!, Border = null!, Track = null!,
        Fill = null!, Hover = null!, Selected = null!, Good = null!, Warn = null!, Bad = null!, Accent = null!, Chip = null!, Card = null!, SidebarGlass = null!;

    public static event Action? Changed;

    public static void Init()
    {
        Load();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var was = (Dark, TaskbarDark);
            Load();
            if (was != (Dark, TaskbarDark)) Application.Current.Dispatcher.Invoke(() => Changed?.Invoke());
        };
    }

    static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static void Load()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        Dark = (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
        TaskbarDark = (k?.GetValue("SystemUsesLightTheme") as int? ?? 0) == 0;

        if (Dark)
        {
            Bg = B("#202124"); Sidebar = B("#1A1B1E"); Text = B("#F1F3F4"); SubText = B("#9AA0A6");
            Border = B("#33363B"); Track = B("#3A3D42"); Fill = B("#E8EAED"); Hover = B("#2C2E33");
            Selected = B("#33363B"); Chip = B("#2A2C31"); Card = B("#14FFFFFF"); SidebarGlass = B("#22000000");
        }
        else
        {
            Bg = B("#FFFFFF"); Sidebar = B("#F6F7F9"); Text = B("#111827"); SubText = B("#4B5A86");
            Border = B("#E3E6EC"); Track = B("#E9EBF0"); Fill = B("#0F1A33"); Hover = B("#EEF0F4");
            Selected = B("#E4E7EE"); Chip = B("#FFFFFF"); Card = B("#B3FFFFFF"); SidebarGlass = B("#0F000000");
        }
        Good = B("#22A447"); Warn = B("#F29D0B"); Bad = B("#E5484D"); Accent = B("#3B6FF5");
    }

    /// <summary>Bar color by how much is left.</summary>
    public static Brush ForLeft(double left) => left <= 10 ? Bad : left <= 25 ? Warn : Fill;
}
