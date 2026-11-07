using System.Runtime.InteropServices;

namespace QuotaBar.App;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static bool ForegroundIsOurs()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    /// <summary>
    /// True when the taskbar on <paramref name="tray"/>'s monitor is not really there: a full-screen app
    /// (game, video, presentation) covers that monitor, or the taskbar is hidden / auto-hidden.
    /// </summary>
    public static bool TaskbarHidden(IntPtr tray)
    {
        if (!IsWindowVisible(tray) || !GetWindowRect(tray, out var bar)) return true;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var trayMon = MonitorFromWindow(tray, 2);
        if (!GetMonitorInfo(trayMon, ref mi)) return false;
        var m = mi.rcMonitor;

        // Auto-hide: the bar slides (almost) off the screen.
        int visW = Math.Min(bar.Right, m.Right) - Math.Max(bar.Left, m.Left);
        int visH = Math.Min(bar.Bottom, m.Bottom) - Math.Max(bar.Top, m.Top);
        if (visW <= 0 || visH <= 0 || (bar.Width > bar.Height ? visH < bar.Height / 2 : visW < bar.Width / 2)) return true;

        // Windows' own full-screen signal (D3D full screen, presentation mode, busy full-screen app).
        if (SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4)
        {
            var fgw = GetForegroundWindow();
            if (fgw == IntPtr.Zero || MonitorFromWindow(fgw, 2) == trayMon) return true;
        }

        // Borderless full-screen window on the taskbar's monitor. Shell windows never count.
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && MonitorFromWindow(fg, 2) == trayMon && !IsShell(fg) && Covers(fg, m)) return true;

        // A full-screen window that has lost focus (video on another monitor while you work on this one)
        // still sits above that monitor's taskbar: look at every window above the taskbar in the z-order.
        uint own = (uint)Environment.ProcessId;
        for (var w = GetWindow(tray, GW_HWNDPREV); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDPREV))
        {
            if (!IsWindowVisible(w) || IsIconic(w) || Cloaked(w) || ProcessOf(w) == own) continue;
            if (Covers(w, m) && !IsShell(w)) return true;
        }
        return false;
    }

    static bool Covers(IntPtr h, RECT m) =>
        GetWindowRect(h, out var r) && r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;

    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    /// <summary>Hidden by DWM though "visible" (suspended store apps, windows on other virtual desktops).</summary>
    static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var c, sizeof(int)) == 0 && c != 0;

    /// <summary>Desktop, taskbar and shell flyouts (Start, calendar, notification center, tray overflow).</summary>
    public static bool IsShell(IntPtr h)
    {
        var cls = ClassOf(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass" or "Windows.UI.Core.CoreWindow") return true;
        GetWindowThreadProcessId(h, out var pid);
        GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out var shellPid);
        return pid == shellPid;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const uint GW_HWNDPREV = 3;

    /// <summary>True when <paramref name="other"/> is above <paramref name="h"/> in the z-order.</summary>
    public static bool IsAbove(IntPtr other, IntPtr h)
    {
        for (var w = GetWindow(h, GW_HWNDPREV); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDPREV))
            if (w == other) return true;
        return false;
    }

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_REORDER = 0x8004,
        EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    /// <summary>Top-left corner of the monitor a window is on.</summary>
    public static System.Drawing.Point? MonitorOrigin(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(h, 2), ref mi) ? new System.Drawing.Point(mi.rcMonitor.Left, mi.rcMonitor.Top) : null;
    }

    public static uint ProcessOf(IntPtr h) { GetWindowThreadProcessId(h, out var pid); return pid; }

    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

    /// <summary>Mica backdrop (Windows 11 22H2+). Returns false when unsupported.</summary>
    public static bool TryMica(IntPtr h)
    {
        if (Environment.OSVersion.Version.Build < 22621) return false;
        var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
        int type = 2; // DWMSBT_MAINWINDOW (Mica)
        return DwmSetWindowAttribute(h, 38, ref type, sizeof(int)) == 0;
    }

    /// <summary>Rounded corners + Mica-ish backdrop on Windows 11 (no-op elsewhere).</summary>
    public static void Win11Chrome(IntPtr h, bool dark)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref d, sizeof(int));
    }
}
