using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PinyinTranslate.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>The modifier state is the state after this key's transition.</summary>
public readonly record struct KeyStroke(uint VirtualKey, bool IsKeyDown, KeyModifiers Modifiers,
    bool IsRepeat, nint ForegroundWindow);

/// <summary>
/// Owns a WH_KEYBOARD_LL hook and its background message loop. Both delegates run
/// inside the hook: they must return promptly, and may only update local state or
/// queue work. They must never invoke UI Automation, network calls, or synchronous
/// UI dispatch. The owner must set <see cref="Enabled"/> to allow Tab acceptance.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012, LlkhfExtended = 0x01;
    private const uint VkTab = 0x09, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const uint VkLWin = 0x5B, VkRWin = 0x5C;
    private const uint VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4, VkRMenu = 0xA5;

    /// <summary>
    /// Set dwExtraInfo to this value on this application's SendInput events.
    /// Only events with this exact tag are ignored; other injected input is
    /// observed normally, including input from the independent test driver.
    /// </summary>
    public static nuint SyntheticInputMarker => 0x5054594E;

    private readonly Func<nint, bool> _tryAcceptTab;
    private readonly Action<KeyStroke> _onKeyStroke;
    private readonly LowLevelKeyboardProc _callback;
    private readonly Thread _thread;
    private readonly bool[] _keysDown = new bool[256];
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hook;
    private uint _threadId;
    private int _enabled, _installed, _disposed, _installationErrorCode;
    private string? _installationError;
    private bool _swallowTabUntilUp;

    public InputKeyboardHook(Func<nint, bool> tryAcceptTab, Action<KeyStroke> onKeyStroke)
    {
        ArgumentNullException.ThrowIfNull(tryAcceptTab);
        ArgumentNullException.ThrowIfNull(onKeyStroke);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Keyboard hooks require Windows.");
        _tryAcceptTab = tryAcceptTab;
        _onKeyStroke = onKeyStroke;
        // Keep the delegate rooted for the full native hook lifetime.
        _callback = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "PinyinTranslate keyboard hook" };
        _thread.Start();
    }

    /// <summary>True when installation completed, false when it failed or was disposed before startup.</summary>
    public Task<bool> Ready => _ready.Task;
    public bool IsInstalled => Volatile.Read(ref _installed) != 0;
    public int InstallationErrorCode => Volatile.Read(ref _installationErrorCode);
    public string? InstallationError => Volatile.Read(ref _installationError);

    /// <summary>
    /// Controls acceptance only. Observations continue while disabled. A Tab
    /// already accepted remains suppressed until its matching key-up.
    /// </summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0 && Volatile.Read(ref _disposed) == 0;
        set => Volatile.Write(ref _enabled, value && Volatile.Read(ref _disposed) == 0 ? 1 : 0);
    }

    private void Run()
    {
        try
        {
            // A thread message queue must exist before Dispose can post WM_QUIT.
            _ = PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (Volatile.Read(ref _disposed) != 0) return;

            // Async state is only sampled at startup. Inside the callback it can
            // still describe the state before the current transition.
            for (int key = 0; key < _keysDown.Length; key++)
                _keysDown[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
            _keysDown[VkShift] = _keysDown[VkControl] = _keysDown[VkMenu] = false;

            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                ReportError(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
                return;
            }

            Volatile.Write(ref _installed, 1);
            _ready.TrySetResult(Volatile.Read(ref _disposed) == 0);
            while (Volatile.Read(ref _disposed) == 0)
            {
                int result = GetMessage(out NativeMessage message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0)
                {
                    ReportError(Marshal.GetLastWin32Error(), "The keyboard hook message loop failed.");
                    break;
                }
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref _installationError, error.Message);
        }
        finally
        {
            Volatile.Write(ref _installed, 0);
            if (_hook != 0)
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
            _ready.TrySetResult(false);
            GC.KeepAlive(_callback);
        }
    }

    private nint HookCallback(int code, nuint message, nint dataPointer)
    {
        if (code < 0) return CallNextHookEx(0, code, message, dataPointer);
        try
        {
            bool keyDown = message == WmKeyDown || message == WmSysKeyDown;
            bool keyUp = message == WmKeyUp || message == WmSysKeyUp;
            if (!keyDown && !keyUp) return CallNextHookEx(0, code, message, dataPointer);

            KeyboardData data = Marshal.PtrToStructure<KeyboardData>(dataPointer);
            if (ProcessKeyEvent(data, keyDown, GetForegroundWindow())) return 1;
        }
        catch (Exception)
        {
            // Fail open for ordinary input; no managed exception crosses user32.
        }
        return CallNextHookEx(0, code, message, dataPointer);
    }

    // State transitions are kept separate from native dispatch so suppression can
    // be verified without installing hooks or sending any keyboard input.
    private bool ProcessKeyEvent(KeyboardData data, bool keyDown, nint foreground)
    {
        if (data.ExtraInfo == SyntheticInputMarker) return false;
        uint key = NormalizeModifier(data.VirtualKey, data.ScanCode, data.Flags);
        if (key >= _keysDown.Length) return false;
        bool repeat = keyDown && _keysDown[key];
        _keysDown[key] = keyDown;

        // Once accepted, consume the complete physical press, even if the
        // enabled state or modifiers change before auto-repeat or key-up.
        if (key == VkTab && _swallowTabUntilUp)
        {
            if (!keyDown) _swallowTabUntilUp = false;
            return true;
        }

        KeyModifiers modifiers = ReadModifiers();
        if (key == VkTab && keyDown && !repeat && modifiers == KeyModifiers.None && foreground != 0 && Enabled)
        {
            bool accepted;
            try { accepted = _tryAcceptTab(foreground); }
            catch (Exception) { accepted = false; }
            if (accepted)
            {
                _swallowTabUntilUp = true;
                return true;
            }
        }

        // Accepted Tab transitions are excluded so they cannot immediately
        // invalidate the queued acceptance. Subscribers classify edit keys.
        try { _onKeyStroke(new(data.VirtualKey, keyDown, modifiers, repeat, foreground)); }
        catch (Exception) { /* Subscriber failures cannot escape a native callback. */ }
        return false;
    }

    private KeyModifiers ReadModifiers()
    {
        KeyModifiers result = KeyModifiers.None;
        if (_keysDown[VkLShift] || _keysDown[VkRShift]) result |= KeyModifiers.Shift;
        if (_keysDown[VkLControl] || _keysDown[VkRControl]) result |= KeyModifiers.Control;
        if (_keysDown[VkLMenu] || _keysDown[VkRMenu]) result |= KeyModifiers.Alt;
        if (_keysDown[VkLWin] || _keysDown[VkRWin]) result |= KeyModifiers.Windows;
        return result;
    }

    private static uint NormalizeModifier(uint key, uint scanCode, uint flags) => key switch
    {
        VkShift => scanCode == 0x36 ? VkRShift : VkLShift,
        VkControl => (flags & LlkhfExtended) != 0 ? VkRControl : VkLControl,
        VkMenu => (flags & LlkhfExtended) != 0 ? VkRMenu : VkLMenu,
        _ => key
    };

    private void ReportError(int code, string description)
    {
        Volatile.Write(ref _installationErrorCode, code);
        Volatile.Write(ref _installationError, $"{description} {new Win32Exception(code).Message} ({code})");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _enabled, 0);
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) _ = PostThreadMessage(threadId, WmQuit, 0, 0);
        // A callback may dispose its own hook; Run's finally handles that case.
        if (Thread.CurrentThread != _thread) _thread.Join();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int code, nuint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? module);
}
