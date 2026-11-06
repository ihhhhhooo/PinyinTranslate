using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PinyinTranslate.Selection;

public enum SelectionActivityKind { PointerDown, PointerUp, KeyDown, KeyUp, PointerWheel }
[Flags]
public enum SelectionActivityModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }

public readonly record struct SelectionActivity(SelectionActivityKind Kind, nint ForegroundWindow,
    nint WindowUnderPointer, int TargetProcessId, int X = 0, int Y = 0, uint VirtualKey = 0,
    SelectionActivityModifiers Modifiers = SelectionActivityModifiers.None, bool IsRepeat = false,
    uint MouseButton = 0, bool CompletesSelectionGesture = false, int WheelDelta = 0,
    long Revision = 0, long Epoch = 0)
{
    public nint TargetRootWindow { get; init; }
    public bool HasCapturedEpoch { get; init; }
    public nint TargetWindow => Kind is SelectionActivityKind.PointerDown or SelectionActivityKind.PointerUp or SelectionActivityKind.PointerWheel
        ? (TargetRootWindow != 0 ? TargetRootWindow : WindowUnderPointer) : ForegroundWindow;
}

public interface ISelectionActivityMonitor : IDisposable
{
    Task<bool> Ready { get; }
    long Revision { get; }
    long Epoch { get; }
    /// <summary>Revision at the mode boundary. Queued sources must retain this value until the next SetEnabled.</summary>
    long EpochStartRevision => Revision;
    event Action<SelectionActivity>? Activity;
    void Start();
    void SetEnabled(bool enabled);
}

/// <summary>Injectable metadata source. Its callback must only invoke fast local-state/queue consumers.</summary>
public interface ISelectionActivitySource : IDisposable
{
    Task<bool> Ready { get; }
    event Action<SelectionActivity>? Activity;
    void Start();
}

/// <summary>Pure key/gesture classification; it does not read or alter system state.</summary>
public sealed class SelectionActivityClassifier
{
    public static nuint SyntheticInputMarker => 0x5054594E;
    private readonly int _ownProcessId;
    private readonly bool[] _down = new bool[256], _selectionKey = new bool[256];
    private readonly (int ProcessId, nint Window)[] _keyOrigins = new (int, nint)[256];
    private readonly Dictionary<uint, (int ProcessId, nint Window)> _pointerOrigins = new();
    public SelectionActivityClassifier(int ownProcessId)
    {
        if (ownProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(ownProcessId));
        _ownProcessId = ownProcessId;
    }

    public void SeedKeyState(uint virtualKey, bool down)
    {
        if (virtualKey < _down.Length && virtualKey is not (0x10 or 0x11 or 0x12)) _down[virtualKey] = down;
    }

    public SelectionActivity? Keyboard(uint virtualKey, bool isDown, uint scanCode, uint flags, nuint extraInfo,
        nint foregroundWindow, int foregroundProcessId)
    {
        if (extraInfo == SyntheticInputMarker) return null;
        uint key = Normalize(virtualKey, scanCode, flags);
        if (key >= _down.Length) return null;
        bool repeat = isDown && _down[key];
        bool completes = !isDown && _selectionKey[key] && _keyOrigins[key] == (foregroundProcessId, foregroundWindow);
        _down[key] = isDown;
        var modifiers = Modifiers;
        if (isDown && StartsSelection(virtualKey, modifiers))
        { _selectionKey[key] = true; _keyOrigins[key] = (foregroundProcessId, foregroundWindow); }
        if (!isDown) _selectionKey[key] = false;
        // Modifier transitions are tracked even over our own window, but clicking
        // or typing in the result's Copy UI must not revoke its external source.
        if (foregroundWindow == 0 || foregroundProcessId <= 0 || foregroundProcessId == _ownProcessId) return null;
        return new(isDown ? SelectionActivityKind.KeyDown : SelectionActivityKind.KeyUp,
            foregroundWindow, 0, foregroundProcessId, VirtualKey: virtualKey, Modifiers: modifiers,
            IsRepeat: repeat, CompletesSelectionGesture: completes);
    }

    public SelectionActivity? Pointer(SelectionActivityKind kind, nint foregroundWindow, nint windowUnderPointer,
        int targetProcessId, int x, int y, uint button, int wheelDelta, nuint extraInfo, nint targetRootWindow = 0)
    {
        if (extraInfo == SyntheticInputMarker || kind is not (SelectionActivityKind.PointerDown or SelectionActivityKind.PointerUp or SelectionActivityKind.PointerWheel)) return null;
        nint target = targetRootWindow != 0 ? targetRootWindow : windowUnderPointer;
        bool completes = false;
        if (kind == SelectionActivityKind.PointerDown && button != 0) _pointerOrigins[button] = (targetProcessId, target);
        if (kind == SelectionActivityKind.PointerUp && button != 0)
            completes = _pointerOrigins.Remove(button, out var origin) && origin == (targetProcessId, target);
        if (targetProcessId <= 0 || targetProcessId == _ownProcessId || windowUnderPointer == 0) return null;
        return new SelectionActivity(kind, foregroundWindow, windowUnderPointer, targetProcessId, x, y, Modifiers: Modifiers,
            MouseButton: button, CompletesSelectionGesture: completes, WheelDelta: wheelDelta) { TargetRootWindow = targetRootWindow };
    }

    public SelectionActivityModifiers Modifiers
    {
        get
        {
            var value = SelectionActivityModifiers.None;
            if (_down[0xA0] || _down[0xA1]) value |= SelectionActivityModifiers.Shift;
            if (_down[0xA2] || _down[0xA3]) value |= SelectionActivityModifiers.Control;
            if (_down[0xA4] || _down[0xA5]) value |= SelectionActivityModifiers.Alt;
            if (_down[0x5B] || _down[0x5C]) value |= SelectionActivityModifiers.Windows;
            return value;
        }
    }

    internal static bool StartsSelection(uint key, SelectionActivityModifiers modifiers) =>
        ((modifiers & SelectionActivityModifiers.Shift) != 0 && key is >= 0x21 and <= 0x28) ||
        (key == 0x41 && (modifiers & SelectionActivityModifiers.Control) != 0 &&
            (modifiers & (SelectionActivityModifiers.Alt | SelectionActivityModifiers.Windows)) == 0);
    private static uint Normalize(uint key, uint scan, uint flags) => key switch
    {
        0x10 => scan == 0x36 ? 0xA1u : 0xA0u,
        0x11 => (flags & 1) != 0 ? 0xA3u : 0xA2u,
        0x12 => (flags & 1) != 0 ? 0xA5u : 0xA4u,
        _ => key
    };
}

/// <summary>
/// Passive activity barrier and asynchronous event queue. Construction installs
/// nothing. Start is explicit; Stop/Dispose is terminal and has bounded native cleanup.
/// Revision changes before subscriber dispatch; Epoch fences enabled sessions.
/// </summary>
public sealed class SelectionActivityMonitor : ISelectionActivityMonitor
{
    private readonly object _gate = new();
    private readonly ISelectionActivitySource _source;
    private readonly Channel<SelectionActivity> _queue;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<uint, (int ProcessId, nint Window)> _pointerGestures = new(), _keyGestures = new();
    private int _ownProcessId, _started, _enabled, _disposed;
    private long _revision, _epoch, _epochStartRevision;
    public SelectionActivityMonitor(ISelectionActivitySource? source = null, int ownProcessId = 0, int queueCapacity = 256)
    {
        if (ownProcessId < 0) throw new ArgumentOutOfRangeException(nameof(ownProcessId));
        if (queueCapacity is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _source = source ?? new NativeSource(() => Epoch);
        _ownProcessId = ownProcessId;
        _queue = Channel.CreateBounded<SelectionActivity>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.DropOldest
        });
        _source.Activity += OnSourceActivity;
    }
    public Task<bool> Ready => _ready.Task;
    public long Revision => Interlocked.Read(ref _revision);
    public long Epoch => Interlocked.Read(ref _epoch);
    public long EpochStartRevision => Interlocked.Read(ref _epochStartRevision);
    public event Action<SelectionActivity>? Activity;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed != 0 || _started != 0) return;
            _started = 1;
            if (_ownProcessId == 0) _ownProcessId = Environment.ProcessId;
        }
        _ = Task.Run(DrainAsync);
        try { _source.Start(); _ = ObserveReadyAsync(); }
        catch (Exception) { _ready.TrySetResult(false); }
    }

    private async Task ObserveReadyAsync()
    {
        bool ready;
        try { ready = await _source.Ready.ConfigureAwait(false); } catch (Exception) { ready = false; }
        lock (_gate) _ready.TrySetResult(ready && _disposed == 0);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _epochStartRevision, Interlocked.Increment(ref _revision));
            _pointerGestures.Clear(); _keyGestures.Clear();
            _enabled = enabled ? 1 : 0;
        }
    }

    private void OnSourceActivity(SelectionActivity activity)
    {
        lock (_gate)
        {
            if (_disposed != 0 || _started == 0 || _enabled == 0 ||
                (activity.HasCapturedEpoch && activity.Epoch != _epoch) ||
                activity.TargetProcessId <= 0 || activity.TargetProcessId == _ownProcessId || activity.TargetWindow == 0 ||
                (int)activity.Kind < (int)SelectionActivityKind.PointerDown || (int)activity.Kind > (int)SelectionActivityKind.PointerWheel) return;
            long revision = Interlocked.Increment(ref _revision);
            var origin = (activity.TargetProcessId, activity.TargetWindow);
            if (activity.Kind == SelectionActivityKind.PointerDown && activity.MouseButton != 0)
                _pointerGestures[activity.MouseButton] = origin;
            if (activity.Kind == SelectionActivityKind.KeyDown && !activity.IsRepeat && SelectionActivityClassifier.StartsSelection(activity.VirtualKey, activity.Modifiers))
                _keyGestures[activity.VirtualKey] = origin;
            if (activity.Kind == SelectionActivityKind.PointerUp)
            {
                bool matched = _pointerGestures.Remove(activity.MouseButton, out var down) && down == origin;
                activity = activity with { CompletesSelectionGesture = activity.CompletesSelectionGesture && matched };
            }
            if (activity.Kind == SelectionActivityKind.KeyUp)
            {
                bool matched = _keyGestures.Remove(activity.VirtualKey, out var keyDown) && keyDown == origin;
                activity = activity with { CompletesSelectionGesture = activity.CompletesSelectionGesture && matched };
            }
            _queue.Writer.TryWrite(activity with { Revision = revision, Epoch = _epoch });
        }
    }

    private async Task DrainAsync()
    {
        await foreach (var activity in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (_disposed != 0 || _enabled == 0 || activity.Epoch != _epoch) continue;
            }
            var subscribers = Activity;
            if (subscribers is null) continue;
            foreach (Action<SelectionActivity> subscriber in subscribers.GetInvocationList())
            {
                lock (_gate)
                {
                    if (_disposed != 0 || _enabled == 0 || activity.Epoch != _epoch) break;
                }
                try { subscriber(activity); } catch (Exception) { }
            }
        }
    }

    public void Stop() => Dispose();
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            _disposed = 1; _enabled = 0;
            Interlocked.Increment(ref _epoch);
            Interlocked.Exchange(ref _epochStartRevision, Interlocked.Increment(ref _revision));
            _ready.TrySetResult(false);
            _queue.Writer.TryComplete();
        }
        _source.Activity -= OnSourceActivity;
        try { _source.Dispose(); } catch (Exception) { }
    }

    private sealed class NativeSource : ISelectionActivitySource
    {
        private readonly Func<long> _captureEpoch;
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HookProcedure _keyboardCallback, _mouseCallback;
        private Thread? _thread;
        private SelectionActivityClassifier? _classifier;
        private nint _keyboardHook, _mouseHook;
        private uint _threadId;
        private int _started, _disposed;
        public NativeSource(Func<long> captureEpoch) { _captureEpoch = captureEpoch; _keyboardCallback = KeyboardCallback; _mouseCallback = MouseCallback; }
        public Task<bool> Ready => _ready.Task;
        public event Action<SelectionActivity>? Activity;
        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _started, 1) != 0) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Selection passive activity hooks" };
            _thread.Start();
        }
        private void Run()
        {
            try
            {
                _ = PeekMessage(out _, 0, 0, 0, 0);
                Volatile.Write(ref _threadId, GetCurrentThreadId());
                if (Volatile.Read(ref _disposed) != 0) return;
                _classifier = new(Environment.ProcessId);
                for (uint key = 0; key < 256; key++) _classifier.SeedKeyState(key, (GetAsyncKeyState((int)key) & 0x8000) != 0);
                if (Volatile.Read(ref _disposed) != 0) return;
                _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
                if (_keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (Volatile.Read(ref _disposed) != 0) return;
                _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
                if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                _ready.TrySetResult(Volatile.Read(ref _disposed) == 0);
                while (Volatile.Read(ref _disposed) == 0)
                {
                    int result = GetMessage(out var message, 0, 0, 0);
                    if (result <= 0) break;
                    _ = TranslateMessage(in message); _ = DispatchMessage(in message);
                }
            }
            catch (Exception) { _ready.TrySetResult(false); }
            finally
            {
                if (_mouseHook != 0) { _ = UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
                if (_keyboardHook != 0) { _ = UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
                _ready.TrySetResult(false);
                GC.KeepAlive(_keyboardCallback); GC.KeepAlive(_mouseCallback);
            }
        }
        private nint KeyboardCallback(int code, nuint message, nint pointer)
        {
            try
            {
                uint messageId = unchecked((uint)message);
                if (code == 0 && _classifier is { } classifier && messageId is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    long epoch = _captureEpoch();
                    var data = Marshal.PtrToStructure<KeyboardData>(pointer);
                    if (data.ExtraInfo != SelectionActivityClassifier.SyntheticInputMarker)
                    {
                        nint foreground = GetForegroundWindow();
                        _ = GetWindowThreadProcessId(foreground, out uint pid);
                        var activity = classifier.Keyboard(data.VirtualKey, messageId is 0x0100 or 0x0104, data.ScanCode, data.Flags,
                            data.ExtraInfo, foreground, unchecked((int)pid));
                        if (activity is { } value) Activity?.Invoke(value with { Epoch = epoch, HasCapturedEpoch = true });
                    }
                }
            }
            catch (Exception) { }
            return CallNextHookEx(0, code, message, pointer);
        }
        private nint MouseCallback(int code, nuint message, nint pointer)
        {
            try
            {
                uint messageId = unchecked((uint)message);
                if (code == 0 && _classifier is { } classifier && TryMouseKind(messageId, out var kind, out uint button))
                {
                    long epoch = _captureEpoch();
                    var data = Marshal.PtrToStructure<MouseData>(pointer);
                    if (data.ExtraInfo != SelectionActivityClassifier.SyntheticInputMarker)
                    {
                        nint underPointer = WindowFromPoint(data.Point);
                        _ = GetWindowThreadProcessId(underPointer, out uint pid);
                        if (messageId is 0x020B or 0x020C) button = (data.MouseDataValue >> 16) == 2 ? 6u : 5u;
                        int wheel = messageId is 0x020A or 0x020E ? unchecked((short)(data.MouseDataValue >> 16)) : 0;
                        var activity = classifier.Pointer(kind, GetForegroundWindow(), underPointer, unchecked((int)pid),
                            data.Point.X, data.Point.Y, button, wheel, data.ExtraInfo, GetAncestor(underPointer, 2));
                        if (activity is { } value) Activity?.Invoke(value with { Epoch = epoch, HasCapturedEpoch = true });
                    }
                }
            }
            catch (Exception) { }
            return CallNextHookEx(0, code, message, pointer);
        }
        private static bool TryMouseKind(uint message, out SelectionActivityKind kind, out uint button)
        {
            button = message switch { 0x0201 or 0x0202 => 1u, 0x0204 or 0x0205 => 2u, 0x0207 or 0x0208 => 4u, _ => 0u };
            kind = message switch
            {
                0x0201 or 0x0204 or 0x0207 or 0x020B => SelectionActivityKind.PointerDown,
                0x0202 or 0x0205 or 0x0208 or 0x020C => SelectionActivityKind.PointerUp,
                0x020A or 0x020E => SelectionActivityKind.PointerWheel,
                _ => (SelectionActivityKind)(-1)
            };
            return (int)kind >= 0;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _ready.TrySetResult(false);
            uint id = Volatile.Read(ref _threadId);
            if (id != 0) _ = PostThreadMessage(id, 0x0012, 0, 0);
            if (_thread is { } thread && thread != Thread.CurrentThread) _ = thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint HookProcedure(int code, nuint message, nint pointer);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint MouseDataValue, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProcedure procedure, nint module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint pointer);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
