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

public interface ICopyClipboardCapture
{
    Task<CopySelectionResult> ReadAsync(CopySelectionRequest request, TimeSpan timeout, CancellationToken cancellation);
}

/// <summary>
/// Inert until an explicit copy request. Production reads run in a disposable
/// child process so a blocked native delayed renderer cannot strand later copies.
/// Injected APIs exercise the same reading algorithm without native services.
/// </summary>
public sealed class WindowsCopiedSelectionReader : ISelectionClipboardReader
{
    public const int MaximumTextLength = 4000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly ICopyClipboardReadApi api;
    private readonly ICopyClipboardCapture? capture;
    private readonly TimeSpan timeout;
    private readonly Func<int, CancellationToken, bool> wait;
    private readonly Func<uint, bool> ignoreOwnSequence;
    private readonly Func<long> clock;
    private readonly BlockingCollection<Action> queue = new(1);
    private readonly SemaphoreSlim access = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object lifecycle = new();
    private Thread? worker;
    private int disposed;

    public WindowsCopiedSelectionReader(ICopyClipboardReadApi? api = null, TimeSpan? timeout = null,
        Func<int, CancellationToken, bool>? wait = null, Func<uint, bool>? ignoreOwnSequence = null,
        ICopyClipboardCapture? capture = null, Func<long>? clock = null)
    {
        this.api = api ?? new NativeApi();
        this.capture = capture ?? (api is null ? new CopyClipboardProcessCapture() : null);
        this.timeout = timeout ?? DefaultTimeout;
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.wait = wait ?? ((milliseconds, token) => !token.WaitHandle.WaitOne(milliseconds));
        this.ignoreOwnSequence = ignoreOwnSequence ?? (_ => false);
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    public async Task<CopySelectionResult> ReadAfterCopyAsync(CopySelectionRequest request, Func<bool> allowed, CancellationToken cancellation)
    {
        if (request.Window == 0 || request.ProcessId <= 0 || request.ProcessId == api.OwnProcessId)
            return new(CopySelectionStatus.ScopeChanged);
        if (cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0) return new(CopySelectionStatus.Cancelled);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        deadline.CancelAfter(timeout);
        CancellationToken token = deadline.Token;
        var completion = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Current() => !token.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && allowed();
        bool queued = false, ownsAccess = false;
        try
        {
            // Wait for the previous helper's actual cleanup, including after its
            // caller timeout, before allowing another clipboard reader to start.
            await access.WaitAsync(token).ConfigureAwait(false);
            ownsAccess = true;
            lock (lifecycle)
            {
                if (disposed != 0) return new(CopySelectionStatus.Cancelled);
                queue.Add(() =>
                {
                    CopySelectionResult result;
                    try
                    {
                        result = capture is null ? ReadOnWorker(request, Current, token) :
                            CaptureOnWorker(request, Current, token);
                    }
                    catch (OperationCanceledException) { result = CancelledResult(); }
                    catch { result = new(CopySelectionStatus.Unavailable); }
                    finally { access.Release(); }
                    completion.TrySetResult(result);
                });
                queued = true; ownsAccess = false;
                if (worker is null)
                {
                    worker = new Thread(Run) { IsBackground = true, Name = "Copied selection reader" };
                    worker.SetApartmentState(ApartmentState.MTA);
                    worker.Start();
                }
            }
            var result = await completion.Task.WaitAsync(token).ConfigureAwait(false);
            return token.IsCancellationRequested ? CancelledResult() : result;
        }
        catch (OperationCanceledException) { return CancelledResult(); }
        finally
        {
            if (ownsAccess) access.Release();
            if (queued) _ = DisposeAfterReadAsync(completion.Task, deadline);
            else deadline.Dispose();
        }

        CopySelectionResult CancelledResult() => new(cancellation.IsCancellationRequested || Volatile.Read(ref disposed) != 0
            ? CopySelectionStatus.Cancelled : CopySelectionStatus.TimedOut);
    }

    private static async Task DisposeAfterReadAsync(Task completion, CancellationTokenSource deadline)
    {
        try { await completion.ConfigureAwait(false); }
        catch { }
        finally { deadline.Dispose(); }
    }

    private CopySelectionResult CaptureOnWorker(CopySelectionRequest request, Func<bool> current, CancellationToken token)
    {
        if (!current()) return new(CopySelectionStatus.Cancelled);
        if (!SameScope(api.GetMetadata(), request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = capture!.ReadAsync(request, timeout, token).GetAwaiter().GetResult();
            if (!current()) return new(CopySelectionStatus.Cancelled);
            if (result.Status != CopySelectionStatus.Success) return result with { Selection = null };
            var copy = result.Selection;
            var after = api.GetMetadata();
            if (!SameScope(after, request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
            if (copy is null || copy.Window != request.Window || copy.ProcessId != request.ProcessId || !ValidText(copy.Text) ||
                copy.Sequence == 0 || ignoreOwnSequence(copy.Sequence)) return new(CopySelectionStatus.NoNewText);
            if (after.Sequence == copy.Sequence && !OwnerAllowed(after)) return new(CopySelectionStatus.NoNewText);
            if (copy.Sequence == request.BeforeSequence)
            {
                if (!copy.SourceOwnedBaseline) return new(CopySelectionStatus.NoNewText);
                // A new copy arrived while the repeat-copy response was in transit.
                if (after.Sequence != copy.Sequence && attempt == 0) continue;
                if (after.Sequence != copy.Sequence || !SourceOwnsClipboard(after, request)) return new(CopySelectionStatus.NoNewText);
            }
            // Text read under the clipboard lock is an immutable snapshot. Java/OLE
            // may finish publishing other formats after it is read; that must not
            // discard the captured text. User activity and foreground still fence it.
            return result;
        }
        return new(CopySelectionStatus.NoNewText);
    }

    internal static WindowsCopiedSelectionReader CreateNativeWorker(int parentProcessId, TimeSpan timeout) =>
        new(new NativeApi(parentProcessId), timeout);

    private CopySelectionResult ReadOnWorker(CopySelectionRequest request, Func<bool> current, CancellationToken token)
    {
        CopySelectionStatus lastStatus = CopySelectionStatus.NoNewText;
        long started = clock();
        while (current())
        {
            var before = api.GetMetadata();
            if (!SameScope(before, request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
            // Prefer fresh data, including delayed rendering. A deliberate repeat
            // copy may reuse stable text still owned by its source after a grace
            // period, giving the application time to publish a new selection.
            if ((!ignoreOwnSequence(before.Sequence) || before.Sequence == request.BeforeSequence) && OwnerAllowed(before))
            {
                var content = api.TryReadText(MaximumTextLength);
                if (content is not null)
                {
                    var after = api.GetMetadata();
                    if (!current() || !SameScope(after, request.Window, request.ProcessId)) return new(CopySelectionStatus.ScopeChanged);
                    bool repeated = content.Sequence == request.BeforeSequence && before.Sequence == content.Sequence &&
                        after.Sequence == content.Sequence && SourceOwnsClipboard(before, request) && SourceOwnsClipboard(after, request) &&
                        clock() - started >= 750;
                    if (content.Sequence != 0 && (content.Sequence != request.BeforeSequence || repeated) &&
                        !ignoreOwnSequence(content.Sequence) && (after.Sequence != content.Sequence || OwnerAllowed(after)))
                    {
                        if (content.Status == CopySelectionStatus.NoText)
                            lastStatus = CopySelectionStatus.NoText; // Some apps publish formats in stages.
                        else
                        {
                            if (content.Status != CopySelectionStatus.Success) return new(content.Status);
                            if (!ValidText(content.Text)) return new(CopySelectionStatus.NoText);
                            return new(CopySelectionStatus.Success, new(request.Window, request.ProcessId, content.Sequence, content.Text!)
                                { SourceOwnedBaseline = repeated });
                        }
                    }
                }
            }
            if (!wait(10, token)) break;
        }
        return new(token.IsCancellationRequested ? CopySelectionStatus.Cancelled : lastStatus);
    }

    public bool IsCurrent(CopiedSelection selection)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        try { return SameScope(api.GetMetadata(), selection.Window, selection.ProcessId); }
        catch { return false; }
    }
    private bool SameScope(CopyClipboardMetadata value, nint window, int process) => window != 0 && process > 0 && process != api.OwnProcessId &&
        value.ForegroundWindow == window && value.ForegroundProcessId == process && !value.ProtectedFocus;
    // Java/OLE/clipboard brokers can own the data in a separate helper window or
    // process. Physical copy + fresh sequence + unchanged foreground is the scope.
    private bool OwnerAllowed(CopyClipboardMetadata value) => value.OwnerProcessId != api.OwnProcessId;
    private bool SourceOwnsClipboard(CopyClipboardMetadata value, CopySelectionRequest request) => value.OwnerProcessId > 0 &&
        OwnerAllowed(value) && (value.OwnerProcessId == request.ProcessId || value.OwnerRoot == request.Window ||
            value.FocusProcessId > 0 && value.OwnerProcessId == value.FocusProcessId);
    private static bool ValidText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) return false;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\0' || char.IsLowSurrogate(text[i])) return false;
            else if (char.IsHighSurrogate(text[i]) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))) return false;
        return true;
    }
    internal static CopyClipboardText ReadUnicodeText(nint memory, nuint byteLength, uint sequence, int maximumLength)
    {
        if (memory == 0 || byteLength < 2) return new(sequence, CopySelectionStatus.NoText);
        // HGLOBAL allocation size includes padding and need not be an even number.
        int length = checked((int)nuint.Min(byteLength / 2, (nuint)(maximumLength + 1)));
        var buffer = new char[length]; Marshal.Copy(memory, buffer, 0, length);
        int end = Array.IndexOf(buffer, '\0');
        if (end < 0) return new(sequence, length > maximumLength ? CopySelectionStatus.TooLong : CopySelectionStatus.NoText);
        return new(sequence, CopySelectionStatus.Success, new(buffer, 0, end));
    }
    private void Run()
    {
        try { foreach (var action in queue.GetConsumingEnumerable()) action(); }
        finally { queue.Dispose(); }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            queue.CompleteAdding();
            if (worker is null) { queue.Dispose(); return; }
        }
        if (Thread.CurrentThread != worker) worker.Join(TimeSpan.FromMilliseconds(200));
    }

    private sealed class NativeApi(int ownProcessId = 0) : ICopyClipboardReadApi
    {
        public int OwnProcessId => ownProcessId > 0 ? ownProcessId : Environment.ProcessId;
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
            // No WM_NULL preflight: a Java/OLE owner need not answer arbitrary
            // messages promptly to provide clipboard data. Native rendering is
            // isolated in the child process and the caller can terminate it.
            if (!OpenClipboard(0)) return null;
            try
            {
                if (!IsClipboardFormatAvailable(13)) return new(GetClipboardSequenceNumber(), CopySelectionStatus.NoText);
                nint handle = GetClipboardData(13);
                if (handle == 0) return null;
                nuint size = GlobalSize(handle);
                if (size < 2) return new(GetClipboardSequenceNumber(), CopySelectionStatus.NoText);
                nint memory = GlobalLock(handle);
                if (memory == 0) return null;
                try
                {
                    return ReadUnicodeText(memory, size, GetClipboardSequenceNumber(), maximumLength);
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
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint handle);
}
