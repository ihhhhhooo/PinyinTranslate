using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace PinyinTranslate.Input;

/// <summary>
/// Win32 clipboard transport. Construction has no OS side effects. Each prepared
/// session owns one temporary message-only window on a pumping background thread;
/// disposing the session closes that thread without automatically restoring data.
/// Authorization is invoked only on TryPrepare's caller thread, never the owner thread.
/// </summary>
public sealed class WindowsClipboardTransport : IClipboardTransport
{
    private const uint CfUnicodeText = 13;
    private const int MaximumFormatBytes = 16 * 1024 * 1024, MaximumTotalBytes = 32 * 1024 * 1024;
    private sealed record SavedFormat(uint Format, byte[] Data);
    private sealed class PreparationFailure(string reason) : Exception(reason) { }

    public ClipboardPreparationResult TryPrepare(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        if (text is null || text.Length > GenericTextInsertionBackend.MaximumTextLength || text.IndexOf('\0') >= 0)
            return new(false, null, "InvalidClipboardText");
        // This delegate may contain caller-owned UIA state. Never capture it in
        // the owner-thread work item below.
        try
        {
            if (cancellationToken.IsCancellationRequested || !stillAuthorized()) return new(false, null, "ClipboardPreparationNotAuthorized");
        }
        catch (Exception) { return new(false, null, "ClipboardAuthorizationFailed"); }

        ClipboardWorker? worker = null;
        try
        {
            worker = new ClipboardWorker();
            var result = worker.Invoke(() => PrepareOnOwner(worker, text, cancellationToken));
            if (!result.Prepared) worker.Dispose();
            return result;
        }
        catch (Exception error)
        {
            worker?.Dispose();
            return new(false, null, "ClipboardPreparationFailed:" + error.GetType().Name);
        }
    }

    private static ClipboardPreparationResult PrepareOnOwner(ClipboardWorker worker, string text, CancellationToken cancellation)
    {
        if (!TryOpen(worker.Window, cancellation)) return new(false, null, "ClipboardBusyOrCancelled");
        var backup = new List<SavedFormat>();
        nint unicode = 0;
        bool cleared = false;
        try
        {
            if (GetClipboardSequenceNumber() == 0) throw new PreparationFailure("ClipboardSequenceUnavailable");
            long total = 0;
            uint format = 0;
            for (int count = 0; ; count++)
            {
                if (count >= 128) throw new PreparationFailure("TooManyClipboardFormats");
                SetLastErrorNative(0);
                format = EnumClipboardFormats(format);
                if (format == 0)
                {
                    if (Marshal.GetLastWin32Error() != 0) throw new PreparationFailure("ClipboardEnumerationFailed");
                    break;
                }
                if (!IsSafeMemoryFormat(format)) throw new PreparationFailure("UnsupportedClipboardHandleFormat");
                nint data = GetClipboardData(format);
                if (data == 0) throw new PreparationFailure("ClipboardFormatUnavailable");
                ulong size = GlobalSize(data).ToUInt64();
                if (size is 0 or > MaximumFormatBytes || (total += (long)size) > MaximumTotalBytes)
                    throw new PreparationFailure("ClipboardBackupTooLargeOrUncopyable");
                nint address = GlobalLock(data);
                if (address == 0) throw new PreparationFailure("ClipboardFormatCannotBeLocked");
                try
                {
                    byte[] bytes = new byte[(int)size];
                    Marshal.Copy(address, bytes, 0, bytes.Length);
                    backup.Add(new(format, bytes));
                }
                finally { _ = GlobalUnlock(data); }
            }

            cancellation.ThrowIfCancellationRequested();
            unicode = Allocate(Encoding.Unicode.GetBytes(text + "\0"));
            cancellation.ThrowIfCancellationRequested();
            if (GetClipboardSequenceNumber() == 0) throw new PreparationFailure("ClipboardSequenceUnavailable");
            if (!EmptyClipboard()) throw new PreparationFailure("ClipboardClearFailed");
            cleared = true;
            if (SetClipboardData(CfUnicodeText, unicode) == 0) throw new PreparationFailure("ClipboardTextSetFailed");
            unicode = 0; // Ownership transferred to the system.
            uint sequence = GetClipboardSequenceNumber();
            if (sequence == 0) throw new PreparationFailure("ClipboardSequenceUnavailableAfterPreparation");
            if (GetClipboardOwner() != worker.Window) throw new PreparationFailure("ClipboardOwnerChangedDuringPreparation");
            return new(true, new ClipboardSession(worker, backup, sequence), "ClipboardPrepared");
        }
        catch (Exception error)
        {
            var restoration = cleared ? RestoreOpen(worker.Window, backup, GetClipboardSequenceNumber()) :
                new ClipboardRestoreResult(ClipboardRestorationStatus.NotUsed, "ClipboardUntouched");
            string reason = error is PreparationFailure failure ? failure.Message : "ClipboardPrepareException:" + error.GetType().Name;
            return new(false, null, reason, restoration.Status);
        }
        finally
        {
            if (unicode != 0) _ = GlobalFree(unicode);
            _ = CloseClipboard();
        }
    }

    private sealed class ClipboardSession(ClipboardWorker worker, IReadOnlyList<SavedFormat> backup, uint sequence) : IClipboardSession
    {
        private readonly nint _preparedOwner = worker.Window;
        private int _disposed, _restoreAttempted;
        private ClipboardRestoreResult? _restoration;
        public bool IsCurrent() => Volatile.Read(ref _disposed) == 0 && sequence != 0 && _preparedOwner != 0 &&
            GetClipboardSequenceNumber() == sequence && GetClipboardOwner() == _preparedOwner && GetClipboardSequenceNumber() == sequence;
        public ClipboardRestoreResult TryRestore()
        {
            if (Volatile.Read(ref _disposed) != 0) return new(ClipboardRestorationStatus.Failed, "ClipboardSessionDisposed");
            if (Interlocked.Exchange(ref _restoreAttempted, 1) != 0)
                return _restoration ?? new(ClipboardRestorationStatus.Failed, "ClipboardRestoreAlreadyAttempted");
            try
            {
                return _restoration = worker.Invoke(() =>
                {
                    if (!TryOpen(worker.Window, CancellationToken.None)) return new ClipboardRestoreResult(ClipboardRestorationStatus.Failed, "ClipboardRestoreBusy");
                    try { return RestoreOpen(worker.Window, backup, sequence); }
                    finally { _ = CloseClipboard(); }
                });
            }
            catch (Exception error) { return _restoration = new(ClipboardRestorationStatus.Failed, "ClipboardRestoreException:" + error.GetType().Name); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) worker.Dispose();
        }
    }

    private static ClipboardRestoreResult RestoreOpen(nint owner, IReadOnlyList<SavedFormat> backup, uint expectedSequence)
    {
        if (expectedSequence == 0) return new(ClipboardRestorationStatus.Failed, "ClipboardSequenceUnavailable");
        if (GetClipboardOwner() != owner || GetClipboardSequenceNumber() != expectedSequence)
            return new(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, "ClipboardChangedPreserved");
        var handles = new List<(uint Format, nint Handle)>();
        try
        {
            // Allocate all restored formats before clearing the temporary text.
            foreach (var format in backup) handles.Add((format.Format, Allocate(format.Data)));
            if (GetClipboardOwner() != owner || GetClipboardSequenceNumber() != expectedSequence)
                return new(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, "ClipboardChangedPreserved");
            if (!EmptyClipboard()) return new(ClipboardRestorationStatus.Failed, "ClipboardRestoreClearFailed");
            for (int index = 0; index < handles.Count; index++)
            {
                var format = handles[index];
                if (SetClipboardData(format.Format, format.Handle) == 0)
                    return new(ClipboardRestorationStatus.Failed, "ClipboardRestoreFormatFailed");
                handles[index] = (format.Format, 0); // System owns successful handles.
            }
            return new(ClipboardRestorationStatus.Restored, "ClipboardRestored");
        }
        catch (Exception error) { return new(ClipboardRestorationStatus.Failed, "ClipboardRestoreFailed:" + error.GetType().Name); }
        finally { foreach (var format in handles) if (format.Handle != 0) _ = GlobalFree(format.Handle); }
    }

    private static bool IsSafeMemoryFormat(uint format)
    {
        // Bitmap, palette, enhanced/metafile, private and owner-display handles
        // cannot be restored by copying HGLOBAL bytes. Reject before EmptyClipboard.
        if (format is 1 or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 13 or 15 or 16 or 17) return true;
        if (format < 0xC000) return false;
        var name = new StringBuilder(256);
        if (GetClipboardFormatName(format, name, name.Capacity) == 0) return false;
        return name.ToString() is "HTML Format" or "Rich Text Format" or "Rich Text Format Without Objects" or
            "PNG" or "image/png" or "text/html" or "text/plain" or "text/uri-list" or
            "UniformResourceLocator" or "UniformResourceLocatorW";
    }

    private static nint Allocate(byte[] bytes)
    {
        nint memory = GlobalAlloc(0x0002 | 0x0040, (nuint)bytes.Length);
        if (memory == 0) throw new OutOfMemoryException();
        nint address = GlobalLock(memory);
        if (address == 0) { _ = GlobalFree(memory); throw new InvalidOperationException("Global memory lock failed."); }
        try { Marshal.Copy(bytes, 0, address, bytes.Length); }
        catch { _ = GlobalUnlock(memory); _ = GlobalFree(memory); throw; }
        _ = GlobalUnlock(memory);
        return memory;
    }

    private static bool TryOpen(nint owner, CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (cancellation.IsCancellationRequested) return false;
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    private sealed class ClipboardWorker : IDisposable
    {
        private const uint WorkMessage = 0x8001, QuitMessage = 0x0012;
        private readonly ConcurrentQueue<IWork> _work = new();
        private readonly ManualResetEventSlim _ready = new();
        private readonly Thread _thread;
        private uint _threadId;
        private int _disposed, _exited;
        private Exception? _startupError;
        public nint Window { get; private set; }
        private interface IWork { void Execute(); void Fail(Exception error); }
        private sealed class Work<T>(Func<T> action) : IWork
        {
            public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Execute()
            {
                if (Completion.Task.IsCompleted) return;
                try { Completion.TrySetResult(action()); } catch (Exception error) { Completion.TrySetException(error); }
            }
            public void Fail(Exception error) => Completion.TrySetException(error);
        }
        public ClipboardWorker()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "PinyinTranslate clipboard owner" };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(2))) { Dispose(); throw new TimeoutException("Clipboard owner did not start."); }
            if (_startupError is not null) { Dispose(); throw new InvalidOperationException("Clipboard owner unavailable.", _startupError); }
        }
        public T Invoke<T>(Func<T> action)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _exited) != 0, this);
            var work = new Work<T>(action);
            _work.Enqueue(work);
            if (Volatile.Read(ref _exited) != 0 || !PostThreadMessage(_threadId, WorkMessage, 0, 0)) work.Fail(new InvalidOperationException("Clipboard worker queue unavailable."));
            return work.Completion.Task.GetAwaiter().GetResult();
        }
        private void Run()
        {
            try
            {
                _ = PeekMessage(out _, 0, 0, 0, 0);
                _threadId = GetCurrentThreadId();
                if (Volatile.Read(ref _disposed) != 0) return;
                Window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
                if (Window == 0) throw new InvalidOperationException("Message-only owner creation failed.");
                _ready.Set();
                while (Volatile.Read(ref _disposed) == 0 && GetMessage(out var message, 0, 0, 0) > 0)
                {
                    if (message.Message == WorkMessage)
                    {
                        while (_work.TryDequeue(out var work))
                            if (Volatile.Read(ref _disposed) != 0) work.Fail(new ObjectDisposedException(nameof(ClipboardWorker))); else work.Execute();
                    }
                    else { _ = TranslateMessage(in message); _ = DispatchMessage(in message); }
                }
            }
            catch (Exception error) { _startupError = error; }
            finally
            {
                Volatile.Write(ref _exited, 1);
                while (_work.TryDequeue(out var work)) work.Fail(new ObjectDisposedException(nameof(ClipboardWorker)));
                if (Window != 0) { _ = DestroyWindow(Window); Window = 0; }
                _ready.Set();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_threadId != 0) _ = PostThreadMessage(_threadId, QuitMessage, 0, 0);
            if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(1))) _ready.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format, StringBuilder name, int count);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void SetLastErrorNative(uint error);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
