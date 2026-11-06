using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace PinyinTranslate.Selection;

public sealed record CopyClipboardMetadata(nint ForegroundWindow, int ForegroundProcessId, uint Sequence,
    int OwnerProcessId, nint OwnerRoot = 0, int FocusProcessId = 0, bool ProtectedFocus = false);
public sealed record CopyClipboardText(uint Sequence, CopySelectionStatus Status, string? Text = null);
public interface ICopyClipboardReadApi
{
    int OwnProcessId { get; }
    CopyClipboardMetadata GetMetadata();
    CopyClipboardText? TryReadText(int maximumLength);
}

/// <summary>
/// Inert until an explicit copy request. One worker reads CF_UNICODETEXT only;
/// no clipboard writer, listener, injected keys or UIA calls. A blocked delayed
/// renderer cannot block UIA or enqueue unlimited work after a caller timeout.
/// </summary>
public sealed class WindowsCopiedSelectionReader : ISelectionClipboardReader
{
    public const int MaximumTextLength = 4000;
    private readonly ICopyClipboardReadApi api;
    private readonly TimeSpan timeout;
    private readonly Func<int, CancellationToken, bool> wait;
    private readonly Func<uint, bool> ignoreOwnSequence;
    private readonly BlockingCollection<Action> queue = new(1);
    private readonly object lifecycle = new();
    private Thread? worker;
    private int disposed, busy;

    public WindowsCopiedSelectionReader(ICopyClipboardReadApi? api = null, TimeSpan? timeout = null,
        Func<int, CancellationToken, bool>? wait = null, Func<uint, bool>? ignoreOwnSequence = null)
    {
        this.api = api ?? new NativeApi();
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(1200);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.wait = wait ?? ((milliseconds, token) => !token.WaitHandle.WaitOne(milliseconds));
        this.ignoreOwnSequence = ignoreOwnSequence ?? (_ => false);
    }

    public async Task<CopySelectionResult> ReadAfterCopyAsync(CopySelectionRequest request, Func<bool> allowed, CancellationToken cancellation)
    {
        if (request.Window == 0 || request.ProcessId <= 0 || request.ProcessId == api.OwnProcessId || request.BeforeSequence == 0)
            return new(CopySelectionStatus.ScopeChanged);
        if (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return new(CopySelectionStatus.Cancelled);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return new(CopySelectionStatus.Busy);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        CancellationToken token = deadline.Token;
        var completion = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Current() => !token.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && allowed();
        lock (lifecycle)
        {
            if (disposed != 0) { Interlocked.Exchange(ref busy, 0); return new(CopySelectionStatus.Cancelled); }
            queue.Add(() =>
            {
                CopySelectionResult result;
                try { result = ReadOnWorker(request, Current, token); }
                catch { result = new(CopySelectionStatus.Unavailable); }
                finally { Interlocked.Exchange(ref busy, 0); }
                completion.TrySetResult(result);
            });
            if (worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "Copied selection reader" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
        }
        try { return await completion.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(cancellation.IsCancellationRequested ? CopySelectionStatus.Cancelled : CopySelectionStatus.TimedOut); }
    }

    private CopySelectionResult ReadOnWorker(CopySelectionRequest request, Func<bool> current, CancellationToken token)
    {
        for (int attempts = 0; attempts < 120 && current(); attempts++)
        {
            var before = api.GetMetadata();
            if (!SameScope(before, request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
            if (before.Sequence != 0 && before.Sequence != request.BeforeSequence && !ignoreOwnSequence(before.Sequence) && OwnerAllowed(before, request))
            {
                var content = api.TryReadText(MaximumTextLength);
                if (content is not null)
                {
                    var after = api.GetMetadata();
                    if (!current() || !SameScope(after, request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
                    if (content.Sequence != 0 && content.Sequence != request.BeforeSequence && after.Sequence == content.Sequence &&
                        !ignoreOwnSequence(content.Sequence) && OwnerAllowed(after, request))
                    {
                        if (content.Status != CopySelectionStatus.Success) return new(content.Status);
                        if (!ValidText(content.Text)) return new(CopySelectionStatus.NoText);
                        return new(CopySelectionStatus.Success, new(request.Window, request.ProcessId, content.Sequence, content.Text!));
                    }
                }
            }
            if (!wait(10, token)) break;
        }
        return new(token.IsCancellationRequested ? CopySelectionStatus.Cancelled : CopySelectionStatus.NoNewText);
    }

    public bool IsCurrent(CopiedSelection selection)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        try { return SameScope(api.GetMetadata(), selection.Window, selection.ProcessId); }
        catch { return false; }
    }
    private bool SameScope(CopyClipboardMetadata value, nint window, int process) => window != 0 && process > 0 && process != api.OwnProcessId &&
        value.ForegroundWindow == window && value.ForegroundProcessId == process && !value.ProtectedFocus;
    private bool OwnerAllowed(CopyClipboardMetadata value, CopySelectionRequest request) => value.OwnerProcessId != api.OwnProcessId &&
        (value.OwnerProcessId == 0 || value.OwnerProcessId == request.ProcessId || value.OwnerRoot == request.Window ||
         value.FocusProcessId > 0 && value.OwnerProcessId == value.FocusProcessId);
    private static bool ValidText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) return false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\0' || char.IsLowSurrogate(text[i])) return false;
            else if (char.IsHighSurrogate(text[i]) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))) return false;
        return true;
    }
    private void Run()
    {
        try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
        finally { Interlocked.Exchange(ref busy, 0); queue.Dispose(); }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            queue.CompleteAdding();
            if (worker is null) { queue.Dispose(); return; }
        }
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromMilliseconds(200));
    }

    private sealed class NativeApi : ICopyClipboardReadApi
    {
        public int OwnProcessId => Environment.ProcessId;
        public CopyClipboardMetadata GetMetadata()
        {
            nint foreground = GetForegroundWindow();
            uint thread = GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            nint owner = GetClipboardOwner(); _ = GetWindowThreadProcessId(owner, out uint ownerProcess);
            var gui = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            int focusProcess = 0; bool password = false;
            if (thread != 0 && GetGUIThreadInfo(thread, ref gui) && gui.Focus != 0 && GetAncestor(gui.Focus, 2) == GetAncestor(foreground, 2))
            {
                _ = GetWindowThreadProcessId(gui.Focus, out uint focused);
                focusProcess = checked((int)focused);
                var name = new StringBuilder(128); GetClassName(gui.Focus, name, name.Capacity);
                string className = name.ToString();
                if (className.Equals("Edit", StringComparison.OrdinalIgnoreCase) || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                    className.StartsWith("WindowsForms10.Edit.", StringComparison.Ordinal))
                    password = (GetWindowLongPtr(gui.Focus, -16).ToInt64() & 0x20) != 0;
            }
            return new(foreground, checked((int)foregroundProcess), GetClipboardSequenceNumber(), checked((int)ownerProcess),
                owner == 0 ? 0 : GetAncestor(owner, 2), focusProcess, password);
        }
        public CopyClipboardText? TryReadText(int maximumLength)
        {
            // Avoid opening the global clipboard when its known owner is hung.
            // WM_NULL is a bounded responsiveness probe, never an input command.
            nint owner = GetClipboardOwner();
            if (owner != 0 && SendMessageTimeout(owner, 0, 0, 0, 0x22, 80, out _) == 0) return null;
            if (!OpenClipboard(0)) return null;
            try
            {
                if (!IsClipboardFormatAvailable(13)) return new(GetClipboardSequenceNumber(), CopySelectionStatus.NoText);
                nint handle = GetClipboardData(13);
                if (handle == 0) return null;
                nuint size = GlobalSize(handle);
                if (size < 2 || size % 2 != 0) return new(GetClipboardSequenceNumber(), CopySelectionStatus.NoText);
                nint memory = GlobalLock(handle);
                if (memory == 0) return null;
                try
                {
                    int length = checked((int)nuint.Min(size / 2, (nuint)(maximumLength + 1)));
                    var buffer = new char[length]; Marshal.Copy(memory, buffer, 0, length);
                    int end = Array.IndexOf(buffer, '\0');
                    if (end < 0) return new(GetClipboardSequenceNumber(), length > maximumLength ? CopySelectionStatus.TooLong : CopySelectionStatus.NoText);
                    return new(GetClipboardSequenceNumber(), CopySelectionStatus.Success, new(buffer, 0, end));
                }
                finally { GlobalUnlock(handle); }
            }
            finally { if (!CloseClipboard()) CloseClipboard(); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rectangle CaretRectangle; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam,
        nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint handle);
}
