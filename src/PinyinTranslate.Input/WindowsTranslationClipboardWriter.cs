using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace PinyinTranslate.Input;

/// <summary>
/// Permanent Unicode clipboard copy, with no backup/restore or history settings.
/// Construction does not call native APIs. First CopyAsync lazily starts one
/// dedicated worker; each completed copy destroys its message-only owner window.
/// A copy task is never timed out while native work continues in the background.
/// </summary>
public sealed class WindowsTranslationClipboardWriter : ITranslationClipboardWriter
{
    public const int MaximumTextLength = 8192;
    private readonly ITranslationClipboardCopyApi _api;
    private readonly TranslationClipboardWriterOptions _options;
    private readonly Action<int> _delay;
    private readonly BlockingCollection<CopyWork> _queue;
    private readonly object _lifecycle = new();
    private Thread? _worker;
    private int _disposed;
    private string? _faultReason;
    private sealed record CopyWork(byte[] UnicodeData, Func<bool> StillCurrent, CancellationToken Token,
        TaskCompletionSource<TranslationClipboardCopyResult> Completion);

    public WindowsTranslationClipboardWriter(ITranslationClipboardCopyApi? api = null,
        TranslationClipboardWriterOptions? options = null, Action<int>? delayMilliseconds = null)
    {
        _api = api ?? new NativeClipboardCopyApi();
        _options = options ?? new();
        if (_options.MaximumOpenAttempts is < 1 or > 64 || _options.RetryDelayMilliseconds is < 0 or > 1000 ||
            _options.QueueCapacity is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options));
        _delay = delayMilliseconds ?? Thread.Sleep;
        _queue = new(_options.QueueCapacity);
    }

    public Task<TranslationClipboardCopyResult> CopyAsync(string text, Func<bool> stillCurrent, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stillCurrent);
        if (!IsValidText(text)) return Task.FromResult(new TranslationClipboardCopyResult(false, false, "InvalidText"));
        if (token.IsCancellationRequested) return Task.FromResult(new TranslationClipboardCopyResult(false, false, "Cancelled"));
        var completion = new TaskCompletionSource<TranslationClipboardCopyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new CopyWork(Encoding.Unicode.GetBytes(text + "\0"), stillCurrent, token, completion);
        lock (_lifecycle)
        {
            if (_disposed != 0 || _faultReason is not null) return Task.FromResult(new TranslationClipboardCopyResult(false, false,
                _faultReason is null ? "WriterDisposed" : "WriterFaulted:" + _faultReason));
            if (!_queue.TryAdd(work)) return Task.FromResult(new TranslationClipboardCopyResult(false, false, "CopyQueueFull"));
            if (_worker is null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "PinyinTranslate translation clipboard" };
                try { _worker.Start(); }
                catch (Exception error)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _queue.CompleteAdding();
                    while (_queue.TryTake(out var pending)) pending.Completion.TrySetResult(new(false, false, "WorkerStartFailed:" + error.GetType().Name));
                }
            }
        }
        return completion.Task;
    }

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                TranslationClipboardCopyResult result;
                try { result = CopyOnWorker(work); }
                catch (Exception error) { result = new(false, true, "UnexpectedCopyFailure:" + error.GetType().Name); }
                work.Completion.TrySetResult(result);
                if (Volatile.Read(ref _faultReason) is not null) break;
            }
        }
        finally
        {
            lock (_lifecycle)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _queue.CompleteAdding();
                string reason = _faultReason is null ? "WorkerStopped" : "WriterFaulted:" + _faultReason;
                while (_queue.TryTake(out var pending)) pending.Completion.TrySetResult(new(false, false, reason));
            }
        }
    }

    private TranslationClipboardCopyResult CopyOnWorker(CopyWork work)
    {
        nint owner = 0, memory = 0;
        bool opened = false, attempted = false;
        string stage = "guard";
        TranslationClipboardCopyResult result = new(false, false, "CopyNotStarted");
        bool Authorized()
        {
            if (Volatile.Read(ref _disposed) != 0 || work.Token.IsCancellationRequested) return false;
            try { return work.StillCurrent() && !work.Token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0; }
            catch (Exception) { return false; }
        }
        TranslationClipboardCopyResult NotCurrent() => new(false, attempted,
            work.Token.IsCancellationRequested ? "Cancelled" : Volatile.Read(ref _disposed) != 0 ? "WriterDisposed" : "ScopeExpired");
        try
        {
            result = Execute();
        }
        catch (Exception error) { result = new(false, attempted, "CopyFailed:" + stage + ":" + error.GetType().Name); }
        finally
        {
            // No restore: fully rendered CF_UNICODETEXT remains system-owned.
            string? cleanupFailure = null;
            if (opened)
            {
                bool closed = false;
                for (int retry = 0; retry < 2; retry++)
                {
                    try { if (_api.Close()) { closed = true; break; } } catch (Exception) { }
                }
                if (!closed) cleanupFailure = "ClipboardCloseFailed";
            }
            if (memory != 0)
            {
                try { _api.FreeData(memory); } catch (Exception) { cleanupFailure ??= "ClipboardMemoryCleanupFailed"; }
            }
            if (owner != 0)
            {
                try { if (!_api.DestroyOwnerWindow(owner)) cleanupFailure ??= "OwnerDestroyFailed"; }
                catch (Exception) { cleanupFailure ??= "OwnerDestroyFailed"; }
            }
            if (cleanupFailure is not null)
            {
                // Never idle on a worker which could still own the clipboard
                // lock/window. Mark terminal, fail pending work and exit the thread.
                Volatile.Write(ref _faultReason, cleanupFailure);
                result = new(false, attempted, cleanupFailure);
            }
        }
        return result;

        TranslationClipboardCopyResult Execute()
        {
            if (!Authorized()) return NotCurrent();
            stage = "create-owner";
            owner = _api.CreateOwnerWindow();
            if (owner == 0) return new(false, false, "OwnerCreationFailed");
            stage = "allocate";
            memory = _api.AllocateUnicodeData(work.UnicodeData);
            if (memory == 0) return new(false, false, "UnicodeAllocationFailed");
            stage = "open";
            for (int attempt = 0; attempt < _options.MaximumOpenAttempts; attempt++)
            {
                if (!Authorized()) return NotCurrent();
                if (_api.TryOpen(owner)) { opened = true; break; }
                if (attempt + 1 < _options.MaximumOpenAttempts && _options.RetryDelayMilliseconds > 0)
                    _delay(_options.RetryDelayMilliseconds);
            }
            if (!opened) return new(false, false, "ClipboardBusy");
            if (!Authorized()) return NotCurrent();
            stage = "empty";
            attempted = true;
            if (!_api.Empty()) return new(false, true, "ClipboardClearFailed");
            // A newer candidate/cancellation can revoke this copy while an old
            // owner handles EmptyClipboard. Do not publish stale text afterwards.
            if (!Authorized()) return NotCurrent();
            stage = "set-unicode";
            if (!_api.SetUnicodeText(memory)) return new(false, true, "UnicodeSetFailed");
            memory = 0; // Ownership transferred; never free or restore published text.
            // Record while we still hold the clipboard lock; querying after
            // closing could accidentally tag somebody else's later copy.
            try { OwnClipboardWrites.Record(_api.GetSequenceNumber()); } catch { }
            return new(true, true, "Copied");
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _queue.CompleteAdding();
            while (_queue.TryTake(out var pending)) pending.Completion.TrySetResult(new(false, false, "WriterDisposed"));
            worker = _worker;
        }
        // This is lifecycle cleanup, never a per-copy timeout or fallback. An
        // already-blocked native call still owns its task and rechecks the guard.
        if (worker is not null && Thread.CurrentThread != worker) _ = worker.Join(TimeSpan.FromSeconds(1));
    }

    private static bool IsValidText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaximumTextLength || text.IndexOf('\0') >= 0) return false;
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private sealed class NativeClipboardCopyApi : ITranslationClipboardCopyApi
    {
        public nint CreateOwnerWindow() => CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        public bool DestroyOwnerWindow(nint owner) => DestroyWindow(owner);
        public bool TryOpen(nint owner) => OpenClipboard(owner);
        public bool Close() => CloseClipboard();
        public bool Empty() => EmptyClipboard();
        public bool SetUnicodeText(nint memory) => SetClipboardData(13, memory) != 0;
        public uint GetSequenceNumber() => GetClipboardSequenceNumber();
        public nint AllocateUnicodeData(ReadOnlyMemory<byte> nullTerminatedUtf16)
        {
            byte[] bytes = nullTerminatedUtf16.ToArray();
            nint memory = GlobalAlloc(0x0002 | 0x0040, (nuint)bytes.Length);
            if (memory == 0) return 0;
            nint address = GlobalLock(memory);
            if (address == 0) { _ = GlobalFree(memory); return 0; }
            try { Marshal.Copy(bytes, 0, address, bytes.Length); }
            catch { _ = GlobalUnlock(memory); _ = GlobalFree(memory); throw; }
            _ = GlobalUnlock(memory);
            return memory;
        }
        public void FreeData(nint memory)
        {
            if (GlobalFree(memory) != 0) throw new InvalidOperationException("Clipboard memory release failed.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
