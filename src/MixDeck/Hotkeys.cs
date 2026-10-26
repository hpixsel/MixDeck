using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MixDeck.Model;

namespace MixDeck;

/// <summary>System-wide hotkeys (RegisterHotKey on a hidden message window). Work in games too.</summary>
public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly HwndSource _window;
    readonly Dictionary<int, HotkeyConfig> _registered = [];
    int _nextId = 1;

    public event Action<HotkeyConfig>? Pressed;

    public HotkeyManager()
    {
        _window = new HwndSource(new HwndSourceParameters("MixDeckHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
    }

    /// <summary>Replaces all registrations. Returns the hotkeys Windows refused (already taken by another app).</summary>
    public List<HotkeyConfig> Register(IEnumerable<HotkeyConfig> hotkeys)
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();

        var failed = new List<HotkeyConfig>();
        foreach (var h in hotkeys)
        {
            if (!TryParse(h.Gesture, out uint mods, out uint vk)) { failed.Add(h); continue; }
            int id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, mods | MOD_NOREPEAT, vk)) _registered[id] = h;
            else
            {
                failed.Add(h);
                Log.Write($"Hotkey {h.Gesture} is taken by another application");
            }
        }
        return failed;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var h))
        {
            handled = true;
            Pressed?.Invoke(h);
        }
        return IntPtr.Zero;
    }

    /// <summary>Builds "Ctrl+Alt+M" from a key event; null while only modifiers are held.</summary>
    public static string? GestureFrom(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None) return null;

        var parts = new List<string>();
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        // A bare letter would steal typing everywhere; allow bare keys only for F13–F24 and media-style keys
        bool bareAllowed = key is >= Key.F13 and <= Key.F24 || key is >= Key.VolumeMute and <= Key.LaunchApplication2;
        if (parts.Count == 0 && !bareAllowed) return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            mods |= p switch { "Ctrl" => MOD_CONTROL, "Alt" => MOD_ALT, "Shift" => MOD_SHIFT, "Win" => MOD_WIN, _ => 0u };
        }
        if (!Enum.TryParse<Key>(parts[^1], out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _window.Dispose();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
