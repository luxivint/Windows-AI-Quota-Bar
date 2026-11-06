using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace QuotaBar.App;

/// <summary>System-wide shortcut (RegisterHotKey) on a message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    const int Id = 0x5142, WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    readonly HwndSource _src;
    readonly Action _pressed;
    bool _registered;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        _src = new HwndSource(new HwndSourceParameters("QuotaBarHotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 });
        _src.AddHook(Hook);
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == Id) { handled = true; _pressed(); }
        return IntPtr.Zero;
    }

    /// <summary>Registers "Ctrl+Alt+Q" style text. Returns null on success, or why it failed. Empty text = off.</summary>
    public string? Set(string? text)
    {
        if (_registered) { UnregisterHotKey(_src.Handle, Id); _registered = false; }
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = MOD_NOREPEAT, vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        if (vk == 0) return L.F("\"{0}\" is not a valid shortcut.", "\"{0}\" geçerli bir kısayol değil.", text);
        _registered = RegisterHotKey(_src.Handle, Id, mods, vk);
        return _registered ? null : L.F("{0} is already used by another app.", "{0} başka bir uygulama tarafından kullanılıyor.", text);
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_src.Handle, Id);
        _src.Dispose();
    }
}
