using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PinyinTranslate.Input;

public enum PointerIntent { ButtonDown, Wheel }
public readonly record struct PointerStroke(PointerIntent Intent, nint ForegroundWindow);

/// <summary>
/// Observes button-down/wheel intent on a dedicated WH_MOUSE_LL message thread.
/// Every event is passed through. The callback must only update local state/queue work.
/// Observation remains active while translation is disabled so an in-flight caret
/// completion cannot override a later pointer action.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InputPointerHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private readonly Action<PointerStroke> _onPointer;
    private readonly LowLevelMouseProc _callback;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hook;
    private uint _threadId;
    private int _installed, _disposed;
    private string? _installationError;

    public InputPointerHook(Action<PointerStroke> onPointer)
    {
        ArgumentNullException.ThrowIfNull(onPointer);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pointer hooks require Windows.");
        _onPointer = onPointer;
        _callback = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "PinyinTranslate pointer hook" };
        _thread.Start();
    }

    public Task<bool> Ready => _ready.Task;
    public bool IsInstalled => Volatile.Read(ref _installed) != 0;
    public string? InstallationError => Volatile.Read(ref _installationError);

    private void Run()
    {
        try
        {
            _ = PeekMessage(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (Volatile.Read(ref _disposed) != 0) return;
            _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Volatile.Write(ref _installationError, new Win32Exception(error).Message + " (" + error + ")");
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
                    int error = Marshal.GetLastWin32Error();
                    Volatile.Write(ref _installationError, new Win32Exception(error).Message + " (" + error + ")");
                    break;
                }
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
        }
        catch (Exception error) { Volatile.Write(ref _installationError, error.GetType().Name); }
        finally
        {
            Volatile.Write(ref _installed, 0);
            if (_hook != 0) { _ = UnhookWindowsHookEx(_hook); _hook = 0; }
            _ready.TrySetResult(false);
            GC.KeepAlive(_callback);
        }
    }

    private nint HookCallback(int code, nuint message, nint dataPointer)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                uint kind = (uint)message;
                bool button = kind is 0x0201 or 0x0203 or 0x0204 or 0x0206 or 0x0207 or 0x0209 or 0x020B or 0x020D;
                bool wheel = kind is 0x020A or 0x020E;
                if (button || wheel)
                {
                    var data = Marshal.PtrToStructure<MouseHookData>(dataPointer);
                    if (data.ExtraInfo != InputKeyboardHook.SyntheticInputMarker)
                        _onPointer(new(button ? PointerIntent.ButtonDown : PointerIntent.Wheel, GetForegroundWindow()));
                }
            }
            catch (Exception) { /* Never let subscriber/marshal errors block pointer input. */ }
        }
        return CallNextHookEx(0, code, message, dataPointer);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ready.TrySetResult(false);
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) _ = PostThreadMessage(threadId, WmQuit, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        // If a subscriber was delayed, Run still owns the hook/delegate and will
        // unhook after returning. Disposed callbacks immediately pass through.
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelMouseProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
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
    private static extern nint SetWindowsHookEx(int hook, LowLevelMouseProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint GetModuleHandle(string? module);
}
