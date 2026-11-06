using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PinyinTranslate.App;

internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Action<int> _fired;
    private AppSettings? _registered;
    public HotkeyManager(Action<int> fired)
    {
        _fired = fired;
        _source = new HwndSource(new HwndSourceParameters("PinyinTranslate hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _source.AddHook(Hook);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WmHotkey) { handled = true; _fired(wp.ToInt32()); }
        return IntPtr.Zero;
    }
    private static (uint modifiers, uint key) Parse(string value)
    {
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException("快捷键应包含修饰键，例如 Ctrl+Alt+T。");
        uint modifiers = 0; Key key = Key.None;
        foreach (var part in parts)
        {
            uint modifier = part.ToLowerInvariant() switch { "ctrl" or "control" => 2u, "alt" => 1u, "shift" => 4u, "win" or "windows" => 8u, _ => 0u };
            if (modifier != 0) { if ((modifiers & modifier) != 0) throw new ArgumentException("快捷键中存在重复修饰键。"); modifiers |= modifier; continue; }
            if (key != Key.None || !Enum.TryParse<Key>(part, true, out key) || key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                throw new ArgumentException("无法识别快捷键：" + value);
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (modifiers == 0 || key == Key.None || vk <= 0) throw new ArgumentException("快捷键必须包含修饰键和一个普通按键。");
        return (modifiers | 0x4000, (uint)vk); // MOD_NOREPEAT
    }
    public string? Apply(AppSettings settings)
    {
        (uint modifiers, uint key)[] bindings;
        try { bindings = [Parse(settings.ImeHotkey), Parse(settings.SelectionHotkey), Parse(settings.ScreenshotHotkey)]; }
        catch (ArgumentException e) { return e.Message; }
        if (bindings.Distinct().Count() != 3) return "三个快捷键不能相同。";
        var previous = _registered;
        UnregisterAll();
        for (int i = 0; i < bindings.Length; i++)
        {
            if (!RegisterHotKey(_source.Handle, i + 1, bindings[i].modifiers, bindings[i].key))
            {
                UnregisterAll(); _registered = null;
                if (previous != null) { var restoreError = Apply(previous); if (restoreError != null) return "快捷键被占用，且原快捷键恢复失败。请重新设置。"; }
                return "快捷键注册失败，可能已被其他程序占用：" + new[] { settings.ImeHotkey, settings.SelectionHotkey, settings.ScreenshotHotkey }[i];
            }
        }
        _registered = settings; return null;
    }
    private void UnregisterAll() { for (int i = 1; i <= 3; i++) UnregisterHotKey(_source.Handle, i); }
    public void Dispose() { UnregisterAll(); _source.RemoveHook(Hook); _source.Dispose(); }
}
