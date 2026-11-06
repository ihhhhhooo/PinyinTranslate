using System.Collections.Concurrent;
using System.Text;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Real copy-worker/state-machine code with a completely managed low-level API.
// No native writer API, clipboard, HWND, hook, input, UIA or network is invoked.
[Trait("Category", "HeadlessOnly")]
public sealed class TranslationClipboardWriterTests
{
    [Fact]
    public async Task An_own_copy_remains_identifiable_after_its_owner_window_has_been_destroyed()
    {
        const uint sequence = 0xF1A0ABCD;
        var api = new FakeApi { Sequence = sequence };
        using var writer = Writer(api);
        Assert.True((await writer.CopyAsync("translated English", () => true, default)).Copied);
        Assert.Equal(0, api.LiveOwners); Assert.True(OwnClipboardWrites.Contains(sequence));
        Assert.False(OwnClipboardWrites.Contains(0));
    }

    [Fact]
    public async Task Failed_writes_do_not_tag_an_unrelated_clipboard_sequence_as_ours()
    {
        const uint sequence = 0xF1A0ABCE;
        var api = new FakeApi { Sequence = sequence, SetSucceeds = false };
        using var writer = Writer(api);
        Assert.False((await writer.CopyAsync("never published", () => true, default)).Copied);
        Assert.False(OwnClipboardWrites.Contains(sequence));
    }
    [Fact]
    public void ConstructionAndUnusedDisposalHaveNoClipboardOrWindowSideEffects()
    {
        var api = new FakeApi();
        using (var writer = Writer(api)) Assert.Empty(api.Calls);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task SuccessfulCopyPersistsAfterOwnerDestructionAndWriterDisposal()
    {
        var api = new FakeApi();
        var writer = Writer(api);
        var result = await writer.CopyAsync("belong to", () => true, default);
        Assert.True(result.Copied); Assert.True(result.ClipboardWriteAttempted);
        Assert.Equal("belong to", api.ClipboardText);
        Assert.Equal(1, api.EmptyCalls); Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.CloseCalls); Assert.Equal(1, api.DestroyCalls);
        Assert.Equal(0, api.FreeCalls); Assert.Equal(0, api.LiveOwners);
        writer.Dispose();
        Assert.Equal("belong to", api.ClipboardText); // No implicit restoration.
    }

    [Fact]
    public async Task ExistingUnsupportedClipboardFormatsDoNotPreventAuthorizedOverwrite()
    {
        var api = new FakeApi { OldClipboardHasBitmapOrPrivateHandles = true };
        using var writer = Writer(api);
        Assert.True((await writer.CopyAsync("hello", () => true, default)).Copied);
        Assert.Equal("hello", api.ClipboardText);
        Assert.Equal(1, api.EmptyCalls); Assert.Equal(1, api.SetCalls);
        // The injected API exposes no enumeration/backup methods at all.
    }

    [Fact]
    public async Task Utf16PayloadIncludesOneTerminalNulAndPreservesSurrogatesAndLineBreaks()
    {
        var api = new FakeApi();
        using var writer = Writer(api);
        const string text = "hello 😀 中文\r\nnext";
        Assert.True((await writer.CopyAsync(text, () => true, default)).Copied);
        Assert.Equal(Encoding.Unicode.GetBytes(text + "\0"), api.LastAllocated);
        Assert.Equal(text, api.ClipboardText);
        Assert.NotEqual((nint)0, api.LastTransferredHandle);
    }

    [Fact]
    public async Task InvalidTextNeverStartsNativeWork()
    {
        string?[] invalid = [null, "", "a\0b", new string('\uD800', 1), new string('\uDC00', 1), new string('x', WindowsTranslationClipboardWriter.MaximumTextLength + 1)];
        var api = new FakeApi();
        using var writer = Writer(api);
        foreach (string? text in invalid)
        {
            var result = await writer.CopyAsync(text!, () => true, default);
            Assert.False(result.Copied); Assert.False(result.ClipboardWriteAttempted); Assert.Equal("InvalidText", result.Reason);
        }
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledOrExpiredScopeDoesNotCreateAnOwner()
    {
        var api = new FakeApi();
        using var writer = Writer(api);
        Assert.False((await writer.CopyAsync("hello", () => true, new CancellationToken(true))).Copied);
        Assert.False((await writer.CopyAsync("hello", () => false, default)).Copied);
        Assert.False((await writer.CopyAsync("hello", () => throw new InvalidOperationException(), default)).Copied);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task BusyClipboardRetriesOnlyTheOpenPhaseAndNeverBacksUpOrRetriesSet()
    {
        var api = new FakeApi();
        api.OpenResults.Enqueue(false); api.OpenResults.Enqueue(false); api.OpenResults.Enqueue(true);
        var delays = new ConcurrentQueue<int>();
        using var writer = Writer(api, new(3, 7, 8), delays.Enqueue);
        Assert.True((await writer.CopyAsync("hello", () => true, default)).Copied);
        Assert.Equal(3, api.OpenCalls); Assert.Equal(new[] { 7, 7 }, delays);
        Assert.Equal(1, api.CreateCalls); Assert.Equal(1, api.AllocateCalls); Assert.Equal(1, api.EmptyCalls); Assert.Equal(1, api.SetCalls);
    }

    [Fact]
    public async Task ExhaustedBusyRetriesLeaveTheOriginalClipboardUntouched()
    {
        var api = new FakeApi { AlwaysBusy = true };
        using var writer = Writer(api, new(3, 0, 8));
        var result = await writer.CopyAsync("hello", () => true, default);
        Assert.False(result.Copied); Assert.False(result.ClipboardWriteAttempted); Assert.Equal("ClipboardBusy", result.Reason);
        Assert.Equal(3, api.OpenCalls); Assert.Equal(0, api.EmptyCalls); Assert.Equal(0, api.SetCalls);
        Assert.Equal("original", api.ClipboardText); Assert.Equal(1, api.FreeCalls); Assert.Equal(1, api.DestroyCalls);
    }

    [Fact]
    public async Task CancellationDuringBusyRetryCannotLaterEmptyOrPublishClipboard()
    {
        var api = new FakeApi { AlwaysBusy = true };
        using var cancellation = new CancellationTokenSource();
        using var writer = Writer(api, new(6, 1, 8), _ => cancellation.Cancel());
        var result = await writer.CopyAsync("hello", () => true, cancellation.Token);
        Assert.False(result.Copied); Assert.False(result.ClipboardWriteAttempted);
        Assert.Equal(1, api.OpenCalls); Assert.Equal(0, api.EmptyCalls); Assert.Equal(0, api.SetCalls);
        Assert.Equal("original", api.ClipboardText);
    }

    [Fact]
    public async Task ScopeIsRecheckedAfterOpenImmediatelyBeforeEmpty()
    {
        var api = new FakeApi();
        bool current = true;
        api.OnOpen = _ => current = false;
        using var writer = Writer(api);
        var result = await writer.CopyAsync("hello", () => current, default);
        Assert.False(result.Copied); Assert.False(result.ClipboardWriteAttempted);
        Assert.Equal(0, api.EmptyCalls); Assert.Equal(0, api.SetCalls); Assert.Equal(1, api.CloseCalls);
        Assert.Equal("original", api.ClipboardText);
    }

    [Fact]
    public async Task ScopeIsRecheckedAfterEmptyImmediatelyBeforeSet()
    {
        var api = new FakeApi();
        bool current = true;
        api.OnEmpty = () => current = false;
        using var writer = Writer(api);
        var result = await writer.CopyAsync("hello", () => current, default);
        Assert.False(result.Copied); Assert.True(result.ClipboardWriteAttempted);
        Assert.Equal(1, api.EmptyCalls); Assert.Equal(0, api.SetCalls); Assert.Equal(1, api.FreeCalls);
        Assert.Equal(1, api.CloseCalls); Assert.Equal(1, api.DestroyCalls);
    }

    [Fact]
    public async Task FailedEmptyNeverCallsSetAndAlwaysReleasesResources()
    {
        var api = new FakeApi { EmptySucceeds = false };
        using var writer = Writer(api);
        var result = await writer.CopyAsync("hello", () => true, default);
        Assert.False(result.Copied); Assert.True(result.ClipboardWriteAttempted); Assert.Equal("ClipboardClearFailed", result.Reason);
        Assert.Equal(0, api.SetCalls); Assert.Equal(1, api.FreeCalls); Assert.Equal(1, api.CloseCalls); Assert.Equal(1, api.DestroyCalls);
        Assert.Equal("original", api.ClipboardText);
    }

    [Fact]
    public async Task FailedSetFreesUntransferredDataAndDoesNotRetryWriting()
    {
        var api = new FakeApi { SetSucceeds = false };
        using var writer = Writer(api);
        var result = await writer.CopyAsync("hello", () => true, default);
        Assert.False(result.Copied); Assert.True(result.ClipboardWriteAttempted); Assert.Equal("UnicodeSetFailed", result.Reason);
        Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.EmptyCalls); Assert.Equal(1, api.FreeCalls);
        Assert.Equal(1, api.CloseCalls); Assert.Equal(1, api.DestroyCalls);
    }

    [Fact]
    public async Task CopyExceptionsReturnFixedMetadataAndReleaseAllocatedResources()
    {
        var api = new FakeApi { ThrowOnOpen = true };
        using var writer = Writer(api);
        var result = await writer.CopyAsync("private translated text", () => true, default);
        Assert.False(result.Copied); Assert.False(result.ClipboardWriteAttempted);
        Assert.Contains("CopyFailed:open:", result.Reason); Assert.DoesNotContain("private translated text", result.Reason);
        Assert.Equal(1, api.FreeCalls); Assert.Equal(1, api.DestroyCalls); Assert.Equal(0, api.CloseCalls);
    }

    [Fact]
    public async Task CloseCanBeRetriedOnceWithoutRepeatingTheClipboardWrite()
    {
        var api = new FakeApi();
        api.CloseResults.Enqueue(false); api.CloseResults.Enqueue(true);
        using var writer = Writer(api);
        Assert.True((await writer.CopyAsync("hello", () => true, default)).Copied);
        Assert.Equal(2, api.CloseCalls); Assert.Equal(1, api.EmptyCalls); Assert.Equal(1, api.SetCalls);
        Assert.Equal(0, api.FreeCalls); Assert.Equal("hello", api.ClipboardText);
    }

    [Fact]
    public async Task UnreleasableClipboardIsNotReportedCopiedAndTheWorkerRejectsNewJobs()
    {
        var api = new FakeApi { CloseSucceeds = false };
        using var writer = Writer(api);
        var first = await writer.CopyAsync("hello", () => true, default);
        Assert.False(first.Copied); Assert.True(first.ClipboardWriteAttempted); Assert.Equal("ClipboardCloseFailed", first.Reason);
        var second = await writer.CopyAsync("must not publish", () => true, default);
        Assert.False(second.Copied); Assert.False(second.ClipboardWriteAttempted); Assert.Contains("WriterFaulted", second.Reason);
        Assert.Equal(2, api.CloseCalls); Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.CreateCalls);
        Assert.Equal(0, api.FreeCalls); // Successful Set transferred ownership even though Close failed.
    }

    [Fact]
    public async Task OwnerDestructionFailureIsTerminalInsteadOfAccumulatingWindows()
    {
        var api = new FakeApi { DestroySucceeds = false };
        using var writer = Writer(api);
        var result = await writer.CopyAsync("hello", () => true, default);
        Assert.False(result.Copied); Assert.Equal("OwnerDestroyFailed", result.Reason);
        Assert.False((await writer.CopyAsync("next", () => true, default)).Copied);
        Assert.Equal(1, api.CreateCalls); Assert.Equal(1, api.DestroyCalls);
    }

    [Fact]
    public async Task FatalCleanupCompletesPendingJobsWithoutStartingAnotherCopy()
    {
        var api = new FakeApi { CloseSucceeds = false };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        api.OnOpen = count => { if (count == 1) { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); } };
        using var writer = Writer(api);
        try
        {
            var first = writer.CopyAsync("first", () => true, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = writer.CopyAsync("must not publish", () => true, default);
            release.Set();
            Assert.False((await first).Copied);
            var pending = await queued.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.Copied); Assert.False(pending.ClipboardWriteAttempted);
            Assert.Contains("WriterFaulted", pending.Reason);
            Assert.Equal(1, api.OpenCalls); Assert.Equal(1, api.SetCalls); Assert.Equal(1, api.CreateCalls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AllCopyIoAndScopeGuardsUseOneDedicatedWorkerThread()
    {
        var api = new FakeApi();
        int caller = Environment.CurrentManagedThreadId;
        var guardThreads = new ConcurrentQueue<int>();
        using var writer = Writer(api);
        for (int count = 0; count < 3; count++)
            Assert.True((await writer.CopyAsync("hello", () => { guardThreads.Enqueue(Environment.CurrentManagedThreadId); return true; }, default)).Copied);
        int worker = Assert.Single(api.Threads.Distinct());
        Assert.NotEqual(caller, worker); Assert.All(guardThreads, thread => Assert.Equal(worker, thread));
        Assert.Equal(3, api.DestroyCalls); Assert.Equal(0, api.LiveOwners);
    }

    [Fact]
    public async Task QueueIsBoundedAndNeverStartsExtraWorkers()
    {
        var api = new FakeApi();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        api.OnOpen = count => { if (count == 1) { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); } };
        using var writer = Writer(api, new(1, 0, 1));
        try
        {
            Task<TranslationClipboardCopyResult> first = writer.CopyAsync("first", () => true, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task<TranslationClipboardCopyResult> second = writer.CopyAsync("second", () => true, default);
            var overflow = await writer.CopyAsync("third", () => true, default);
            Assert.False(overflow.Copied); Assert.Equal("CopyQueueFull", overflow.Reason);
            release.Set();
            Assert.True((await first).Copied); Assert.True((await second).Copied);
            Assert.Single(api.Threads.Distinct()); Assert.Equal(2, api.SetCalls); Assert.Equal("second", api.ClipboardText);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellingBlockedNativeWorkDoesNotPretendTheTaskHasFinishedAndCannotLateSet()
    {
        var api = new FakeApi();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        api.OnEmpty = () => { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); };
        using var writer = Writer(api);
        try
        {
            var copying = writer.CopyAsync("hello", () => true, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            Assert.False(copying.IsCompleted);
            release.Set();
            var result = await copying;
            Assert.False(result.Copied); Assert.True(result.ClipboardWriteAttempted); Assert.Equal(0, api.SetCalls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task DisposeDrainsPendingWorkAndRevokesTheInflightCopyBeforeMutation()
    {
        var api = new FakeApi();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        api.OnOpen = count => { if (count == 1) { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); } };
        var writer = Writer(api);
        try
        {
            var first = writer.CopyAsync("first", () => true, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = writer.CopyAsync("queued", () => true, default);
            var disposing = Task.Run(writer.Dispose);
            var pending = await queued.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.Copied); Assert.False(pending.ClipboardWriteAttempted);
            Assert.False(first.IsCompleted);
            release.Set();
            Assert.False((await first).Copied);
            await disposing;
            Assert.Equal(0, api.EmptyCalls); Assert.Equal(0, api.SetCalls);
            Assert.False((await writer.CopyAsync("later", () => true, default)).Copied);
        }
        finally { release.Set(); writer.Dispose(); }
    }

    private static WindowsTranslationClipboardWriter Writer(FakeApi api, TranslationClipboardWriterOptions? options = null, Action<int>? delay = null) =>
        new(api, options ?? new(3, 0, 8), delay ?? (_ => { }));

    private sealed class FakeApi : ITranslationClipboardCopyApi
    {
        private readonly Dictionary<nint, byte[]> _memory = new();
        private readonly HashSet<nint> _transferred = [];
        private int _nextMemory = 100, _nextOwner = 200;
        private byte[] _clipboard = Encoding.Unicode.GetBytes("original\0");
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<int> Threads { get; } = new();
        public Queue<bool> OpenResults { get; } = new();
        public Queue<bool> CloseResults { get; } = new();
        public int CreateCalls, DestroyCalls, AllocateCalls, OpenCalls, CloseCalls, EmptyCalls, SetCalls, FreeCalls, LiveOwners;
        public bool AlwaysBusy, ThrowOnOpen, OldClipboardHasBitmapOrPrivateHandles;
        public bool EmptySucceeds = true, SetSucceeds = true, CloseSucceeds = true, DestroySucceeds = true;
        public Action<int>? OnOpen;
        public Action? OnEmpty;
        public byte[]? LastAllocated;
        public nint LastTransferredHandle;
        public uint Sequence;
        public uint GetSequenceNumber() => Sequence;
        public string ClipboardText => Encoding.Unicode.GetString(_clipboard).TrimEnd('\0');
        private void Call(string name) { Calls.Enqueue(name); Threads.Enqueue(Environment.CurrentManagedThreadId); }
        public nint CreateOwnerWindow() { Call("create"); CreateCalls++; LiveOwners++; return ++_nextOwner; }
        public bool DestroyOwnerWindow(nint owner) { Call("destroy"); DestroyCalls++; if (DestroySucceeds) LiveOwners--; return DestroySucceeds; }
        public nint AllocateUnicodeData(ReadOnlyMemory<byte> data)
        {
            Call("allocate"); AllocateCalls++; LastAllocated = data.ToArray();
            nint memory = ++_nextMemory; _memory.Add(memory, LastAllocated); return memory;
        }
        public void FreeData(nint memory)
        {
            Call("free"); FreeCalls++;
            if (_transferred.Contains(memory)) throw new InvalidOperationException("Must not free system-owned data.");
            Assert.True(_memory.Remove(memory));
        }
        public bool TryOpen(nint owner)
        {
            Call("open"); OpenCalls++; OnOpen?.Invoke(OpenCalls);
            if (ThrowOnOpen) throw new InvalidOperationException("fake failure");
            return !AlwaysBusy && (OpenResults.Count == 0 || OpenResults.Dequeue());
        }
        public bool Close() { Call("close"); CloseCalls++; return CloseResults.Count == 0 ? CloseSucceeds : CloseResults.Dequeue(); }
        public bool Empty()
        {
            Call("empty"); EmptyCalls++;
            if (!EmptySucceeds) return false;
            _clipboard = [];
            OnEmpty?.Invoke(); return true;
        }
        public bool SetUnicodeText(nint memory)
        {
            Call("set"); SetCalls++;
            if (!SetSucceeds) return false;
            _clipboard = _memory[memory].ToArray(); _transferred.Add(memory); LastTransferredHandle = memory;
            return true;
        }
    }
}
