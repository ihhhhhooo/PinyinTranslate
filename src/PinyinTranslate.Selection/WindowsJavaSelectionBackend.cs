using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PinyinTranslate.Selection;

/// <summary>
/// Lazy, read-only Java Access Bridge client. The DLL is loaded from the target
/// application's runtime or System32, never from PATH/current directory. A
/// dedicated message-pumping thread owns every DLL call and Java reference.
/// No JAB/IDE settings are changed and no external program is launched.
/// </summary>
internal sealed class WindowsJavaSelectionBackend : IJavaSelectionBackend
{
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private Thread? worker;
    private Action? pending;
    private bool busy, disposed;
    private NativeAccess? api;
    private JavaSelectionCapture? capture;
    private long retryLoadAt;
    private string? unavailableReason;
    public string? UnavailableReason => Volatile.Read(ref unavailableReason);

    public JavaSelectionWindow? GetForegroundJavaWindow()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == Environment.ProcessId) return null;
        var name = new StringBuilder(128);
        if (GetClassName(window, name, name.Capacity) == 0 || name.ToString() is not ("SunAwtFrame" or "SunAwtDialog" or "SunAwtWindow")) return null;
        return new(window, checked((int)pid));
    }

    public JavaSelectedText? Read(JavaSelectionWindow window, int? x, int? y, Func<bool> allowed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(650));
        CancellationToken deadlineToken = deadline.Token;
        var completion = new TaskCompletionSource<JavaSelectedText?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Current() => !deadlineToken.IsCancellationRequested && !Volatile.Read(ref disposed) && allowed() && GetForegroundJavaWindow() == window;
        lock (gate)
        {
            if (disposed || busy) return null;
            busy = true;
            // Only the token crosses the queue; the caller may dispose its CTS
            // after a timeout while the native owner is still finishing a call.
            pending = () =>
            {
                try
                {
                    if (!Current()) { completion.TrySetResult(null); return; }
                    if (api is null && Environment.TickCount64 >= retryLoadAt)
                    {
                        retryLoadAt = Environment.TickCount64 + 15000;
                        api = NativeAccess.TryLoad(window.ProcessId);
                        if (api is not null) capture = new(api);
                    }
                    if (capture is null)
                    {
                        Volatile.Write(ref unavailableReason, "无法加载 IDEA/Java 的选区组件。请确认 IDE 自带运行时的 WindowsAccessBridge-64.dll 可用，并开启屏幕阅读器支持；也可使用截图翻译。");
                        completion.TrySetResult(null); return;
                    }
                    var result = capture.Read(window.Window, x, y, Current);
                    if (Current()) Volatile.Write(ref unavailableReason, capture.UnavailableReason);
                    completion.TrySetResult(Current() ? result : null);
                }
                catch { completion.TrySetResult(null); }
                finally { lock (gate) busy = false; }
            };
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Java selection bridge" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
            wake.Set();
        }
        try { return completion.Task.WaitAsync(deadlineToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref unavailableReason, "IDEA/Java 选区读取超时，本轮已结束；请重新划选或使用截图翻译。");
            return null;
        }
    }

    private void Run()
    {
        try
        {
            while (!Volatile.Read(ref disposed))
            {
                // WindowsAccessBridge creates a hidden IPC window on this
                // thread. Pump messages even when there are no read requests.
                while (PeekMessage(out var message, 0, 0, 0, 1))
                { TranslateMessage(ref message); DispatchMessage(ref message); }
                Action? action;
                lock (gate) { action = pending; pending = null; }
                action?.Invoke();
                wake.WaitOne(20);
            }
        }
        finally
        {
            capture?.Dispose(); api?.Dispose();
            lock (gate) { pending = null; busy = false; }
            wake.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            Volatile.Write(ref disposed, true);
            if (worker is null) { wake.Dispose(); return; }
            wake.Set();
        }
        // An unresponsive provider retains ownership until its call returns.
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromMilliseconds(300));
    }

    private sealed class NativeAccess : IJavaTextAccess, IDisposable
    {
        private readonly nint library;
        private readonly ContextFromWindow root, focus;
        private readonly ContextAt at;
        private readonly ParentContext parent;
        private readonly ContextInformation info;
        private readonly TextSelection selection;
        private readonly TextRange text;
        private readonly SameObject same;
        private readonly ReleaseObject release;

        private NativeAccess(nint library)
        {
            this.library = library;
            root = Export<ContextFromWindow>("getAccessibleContextFromHWND");
            focus = Export<ContextFromWindow>("getAccessibleContextWithFocus");
            at = Export<ContextAt>("getAccessibleContextAt");
            parent = Export<ParentContext>("getAccessibleParentFromContext");
            info = Export<ContextInformation>("getAccessibleContextInfo");
            selection = Export<TextSelection>("getAccessibleTextSelectionInfo");
            text = Export<TextRange>("getAccessibleTextRange");
            same = Export<SameObject>("isSameObject");
            release = Export<ReleaseObject>("releaseJavaObject");
            Export<WindowsRun>("Windows_run")();
        }
        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        public static NativeAccess? TryLoad(int processId)
        {
            if (!Environment.Is64BitProcess) return null;
            var candidates = new List<string>();
            try
            {
                using var process = Process.GetProcessById(processId);
                string? bin = Path.GetDirectoryName(process.MainModule?.FileName);
                if (bin is not null)
                {
                    candidates.Add(Path.Combine(bin, "WindowsAccessBridge-64.dll"));
                    candidates.Add(Path.GetFullPath(Path.Combine(bin, "..", "jbr", "bin", "WindowsAccessBridge-64.dll")));
                    candidates.Add(Path.GetFullPath(Path.Combine(bin, "..", "jre", "bin", "WindowsAccessBridge-64.dll")));
                }
            }
            catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsAccessBridge-64.dll"));
            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
                nint handle = LoadLibraryEx(path, 0, 0x1100); // DLL_LOAD_DIR | DEFAULT_DIRS
                if (handle == 0) continue;
                try { return new(handle); }
                catch { FreeLibrary(handle); }
            }
            return null;
        }

        public JavaContext? GetRoot(nint window) => root(window, out int vm, out long context) && context != 0 ? new(vm, context) : null;
        public JavaContext? GetFocus(nint window) => focus(window, out int vm, out long context) && context != 0 ? new(vm, context) : null;
        public JavaContext? GetAt(JavaContext root, int x, int y) => at(root.VmId, root.ObjectId, x, y, out long context) && context != 0 ? new(root.VmId, context) : null;
        public JavaContext? GetParent(JavaContext context)
        { long value = parent(context.VmId, context.ObjectId); return value == 0 ? null : new(context.VmId, value); }
        public JavaContextInfo? GetInfo(JavaContext context) => info(context.VmId, context.ObjectId, out var value)
            ? new(value.RoleEnglish ?? "", value.StatesEnglish ?? "", value.AccessibleText != 0, new(value.X, value.Y, value.Width, value.Height)) : null;
        public (int Start, int End)? GetSelection(JavaContext context) => selection(context.VmId, context.ObjectId, out var value) ? (value.Start, value.End) : null;
        public string? GetText(JavaContext context, int start, int end)
        {
            int length = end - start;
            if (start < 0 || length is <= 0 or > JavaSelectionCapture.MaximumTextLength) return null;
            var buffer = new StringBuilder(length + 1);
            // Selection's end is exclusive; JAB's text-range end is inclusive.
            return text(context.VmId, context.ObjectId, start, end - 1, buffer, checked((short)(length + 1))) ? buffer.ToString() : null;
        }
        public bool IsSame(JavaContext left, JavaContext right) => left.VmId == right.VmId && same(left.VmId, left.ObjectId, right.ObjectId);
        public void Release(JavaContext context) => release(context.VmId, context.ObjectId);
        public void Dispose() => FreeLibrary(library);

        // ABI: Win32 long/BOOL/jint are 32-bit; JOBJECT64 is 64-bit. wchar_t is UTF-16.
        // https://docs.oracle.com/en/java/javase/17/access/java-access-bridge-api.html
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ContextInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string Description;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Role;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string RoleEnglish;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string States;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string StatesEnglish;
            public int Index, Children, X, Y, Width, Height;
            public int AccessibleComponent, AccessibleAction, AccessibleSelection, AccessibleText, AccessibleInterfaces;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SelectionInfo
        {
            public int Start, End;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string SelectedText;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WindowsRun();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ContextFromWindow(nint window, out int vm, out long context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ContextAt(int vm, long parent, int x, int y, out long context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long ParentContext(int vm, long context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool ContextInformation(int vm, long context, out ContextInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool TextSelection(int vm, long context, out SelectionInfo selection);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool TextRange(int vm, long context, int start, int inclusiveEnd, StringBuilder text, short length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private delegate bool SameObject(int vm, long left, long right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseObject(int vm, long context);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint library);
}
