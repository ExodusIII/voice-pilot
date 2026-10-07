using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace VoicePilot.App;

/// A system-wide hotkey (works while any app is focused) via RegisterHotKey.
public sealed class Hotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const int Id = 0x5650;   // "VP"

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly IntPtr _hwnd;
    readonly HwndSource _source;
    bool _registered;

    public event Action? Pressed;
    public string? Current { get; private set; }

    public Hotkey(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    /// Registers e.g. "Ctrl+Alt+Space". Returns an error message, or null on success.
    public string? Set(string combo)
    {
        if (_registered) { UnregisterHotKey(_hwnd, Id); _registered = false; }
        Current = null;
        if (!TryParse(combo, out var mods, out var vk)) return $"Unrecognised shortcut: {combo}";
        if (!RegisterHotKey(_hwnd, Id, mods | MOD_NOREPEAT, vk))
            return $"{combo} is already used by another app.";
        _registered = true;
        Current = combo;
        return null;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": case "windows": mods |= MOD_WIN; continue;
            }
            if (vk != 0) return false;
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        // A bare letter would block typing; require a modifier unless it's a function/special key.
        var special = vk is >= 0x70 and <= 0x87 or 0x13 or 0x91 or 0x2D;
        return vk != 0 && (mods != 0 || special);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_hwnd, Id);
        _source.RemoveHook(WndProc);
    }
}
