using System.Collections.Concurrent;
using System.Threading.Channels;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// Actual monitor + managed sources only; no clipboard, UIA, native hook or window.
public sealed class SelectionMonitorCopyTests
{
    [Fact]
    public async Task The_monitor_deadline_cancels_the_underlying_reader_and_keeps_its_token_alive_until_it_finishes()
    {
        var reply = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture();
        fixture.Clipboard.ReadOverride = (_, _, token) => { token.Register(() => cancelled.TrySetResult()); return reply.Task; };
        fixture.Monitor.StatusChanged += message => { if (message.Contains("本次复制", StringComparison.Ordinal)) notice.TrySetResult(); };
        await fixture.Enable(); fixture.Copy();
        var request = Assert.Single(fixture.Clipboard.Requests);
        try
        {
            await notice.Task.WaitAsync(TimeSpan.FromSeconds(12));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(request.Token.IsCancellationRequested);
            Assert.Null(Record.Exception(() => _ = request.Token.WaitHandle));
        }
        finally { reply.TrySetResult(new(CopySelectionStatus.Cancelled)); }
        Assert.Empty(fixture.Published);
    }

    [Fact]
    public async Task An_opaque_editor_can_translate_a_manual_copy_without_Uia_validation()
    {
        using var fixture = new Fixture(); await fixture.Enable();
        fixture.Gesture(); fixture.Copy();
        var snapshot = await fixture.Next();
        Assert.True(snapshot.IsCopiedText); Assert.Equal("copied from opaque editor", snapshot.Text);
        Assert.Single(fixture.Clipboard.Requests); Assert.True(fixture.Monitor.IsCurrent(snapshot.Version));
        Assert.True(await fixture.Monitor.ValidateAsync(snapshot)); Assert.Equal(0, fixture.Reader.Validations);
        int reads = fixture.Reader.Reads;
        await fixture.Monitor.RequestReadAsync();
        Assert.True(fixture.Monitor.IsCurrent(snapshot.Version)); Assert.Equal(reads, fixture.Reader.Reads);
        fixture.Clipboard.ScopeCurrent = false;
        Assert.False(fixture.Monitor.IsCurrent(snapshot.Version));
        await fixture.Monitor.RequestReadAsync();
        Assert.False(await fixture.Monitor.ValidateAsync(snapshot));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("no-native-copy-baseline")]
    [InlineData("injected-copy")]
    public async Task Unrelated_input_does_not_start_a_clipboard_read(string scenario)
    {
        using var fixture = new Fixture(); await fixture.Enable();
        fixture.Gesture();
        if (scenario == "off") fixture.Monitor.SetEnabled(false);
        fixture.Copy(window: 100,
            baseline: scenario != "no-native-copy-baseline", injected: scenario == "injected-copy");
        Assert.Empty(fixture.Clipboard.Requests); Assert.Empty(fixture.Published);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    public async Task Ctrl_C_in_any_external_window_needs_no_prior_selection_gesture(int window)
    {
        using var fixture = new Fixture(); await fixture.Enable();
        fixture.Copy(window: window);
        var snapshot = await fixture.Next();
        Assert.True(snapshot.IsCopiedText);
        Assert.Equal(window, snapshot.WindowHandle);
        Assert.Equal("copied from opaque editor", snapshot.Text);
    }

    [Fact]
    public async Task Closing_the_mode_during_capture_cancels_and_fences_a_late_response()
    {
        var reply = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(); fixture.Clipboard.ReadOverride = (_, _, _) => reply.Task;
        await fixture.Enable(); fixture.Gesture(); fixture.Copy();
        var pending = Assert.Single(fixture.Clipboard.Requests);
        Assert.True(pending.Current());
        fixture.Monitor.SetEnabled(false);
        Assert.True(pending.Token.IsCancellationRequested); Assert.False(pending.Current());
        reply.TrySetResult(new(CopySelectionStatus.Success, new(100, 42, 11, "late copied text")));
        Assert.Empty(fixture.Published);
    }

    [Fact]
    public async Task Pause_and_resume_reject_old_copy_key_ups_but_accept_a_fresh_copy_without_reselection()
    {
        using var fixture = new Fixture(); await fixture.Enable();
        fixture.Activity.Send(SelectionActivityKind.KeyDown, 0x43, SelectionActivityModifiers.Control, baseline: true);
        var old = fixture.Activity.Create(SelectionActivityKind.KeyUp, 0x43);
        fixture.Monitor.SetEnabled(false); fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        fixture.Activity.Deliver(old);
        Assert.Empty(fixture.Clipboard.Requests);
        fixture.Copy();
        Assert.True((await fixture.Next()).IsCopiedText);
    }

    [Fact]
    public async Task A_second_copy_cancels_the_first_capture_and_only_the_new_copy_is_published()
    {
        var firstReply = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture();
        int calls = 0;
        fixture.Clipboard.ReadOverride = (request, _, _) => ++calls == 1 ? firstReply.Task :
            Task.FromResult(new CopySelectionResult(CopySelectionStatus.Success, new(request.Window, request.ProcessId, 12, "second copy")));
        await fixture.Enable(); fixture.Copy();
        var first = Assert.Single(fixture.Clipboard.Requests);
        fixture.Copy();
        var snapshot = await fixture.Next();
        Assert.True(first.Token.IsCancellationRequested); Assert.False(first.Current());
        firstReply.TrySetResult(new(CopySelectionStatus.Success, new(100, 42, 11, "stale first copy")));
        Assert.Equal("second copy", snapshot.Text); Assert.Single(fixture.Published);
    }

    [Fact]
    public async Task A_slow_copy_is_not_cancelled_by_the_old_two_second_monitor_deadline()
    {
        var reply = new TaskCompletionSource<CopySelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(); fixture.Clipboard.ReadOverride = (_, _, _) => reply.Task;
        await fixture.Enable(); fixture.Copy();
        await Task.Delay(2100);
        reply.TrySetResult(new(CopySelectionStatus.Success, new(100, 42, 11, "slow renderer text")));
        Assert.Equal("slow renderer text", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task Copied_text_needs_no_work_from_a_blocked_Uia_reader()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(); await fixture.Enable();
        fixture.Reader.ReadOverride = () => { entered.Set(); release.Wait(); return null; };
        Task blocked = fixture.Monitor.RequestReadAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            fixture.Gesture(); fixture.Copy();
            var snapshot = await fixture.Next();
            Assert.True(await fixture.Monitor.ValidateAsync(snapshot)); Assert.Equal(0, fixture.Reader.Validations);
            Assert.Equal("copied from opaque editor", snapshot.Text);
        }
        finally { release.Set(); await blocked; }
    }

    [Fact]
    public async Task Manual_copy_is_ready_even_when_Uia_initialization_is_unresponsive()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var reader = new Reader { StartOverride = () => { entered.Set(); release.Wait(); return true; } };
        using var fixture = new Fixture(reader);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await fixture.Enable(); fixture.Gesture(); fixture.Copy();
            var snapshot = await fixture.Next();
            Assert.True(await fixture.Monitor.ValidateAsync(snapshot)); Assert.Equal(0, reader.Reads);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(CopySelectionStatus.NoNewText)]
    [InlineData(CopySelectionStatus.NoText)]
    [InlineData(CopySelectionStatus.TooLong)]
    [InlineData(CopySelectionStatus.TimedOut)]
    public async Task Failed_copy_has_a_terminal_notice_and_never_publishes_old_text(CopySelectionStatus status)
    {
        using var fixture = new Fixture();
        fixture.Clipboard.ReadOverride = (_, _, _) => Task.FromResult(new CopySelectionResult(status));
        var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Monitor.StatusChanged += message => { if (message.Contains("复制", StringComparison.Ordinal)) notice.TrySetResult(message); };
        await fixture.Enable(); fixture.Gesture(); fixture.Copy();
        Assert.NotEmpty(await notice.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(fixture.Published);
    }

    private sealed record Request(CopySelectionRequest Copy, Func<bool> Current, CancellationToken Token);
    private sealed class ClipboardReader : ISelectionClipboardReader
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        public bool ScopeCurrent = true;
        public Func<CopySelectionRequest, Func<bool>, CancellationToken, Task<CopySelectionResult>>? ReadOverride;
        public Task<CopySelectionResult> ReadAfterCopyAsync(CopySelectionRequest request, Func<bool> current, CancellationToken token)
        {
            Requests.Enqueue(new(request, current, token));
            return ReadOverride?.Invoke(request, current, token) ?? Task.FromResult(new CopySelectionResult(CopySelectionStatus.Success,
                new(request.Window, request.ProcessId, request.BeforeSequence + 1, "copied from opaque editor")));
        }
        public bool IsCurrent(CopiedSelection selection) => ScopeCurrent;
        public void Dispose() { }
    }
    private sealed class Reader : ISelectionReader
    {
        public Func<bool>? StartOverride;
        public Func<SelectionReadResult?>? ReadOverride;
        public int Reads, Validations;
        public event Action<SelectionReaderSignal>? Changed { add { } remove { } }
        public bool Start() => StartOverride?.Invoke() ?? true;
        public void SetEnabled(bool enabled, long epoch) { }
        public SelectionReadResult? Read(SelectionReadRequest request) { Interlocked.Increment(ref Reads); return ReadOverride?.Invoke(); }
        public bool Validate(SelectionReadResult result) { Interlocked.Increment(ref Validations); return false; }
        public void Dispose() { }
    }
    private sealed class FakeActivity : ISelectionActivityMonitor
    {
        private long revision, epoch;
        public long Revision => Interlocked.Read(ref revision);
        public long Epoch => Interlocked.Read(ref epoch);
        public long EpochStartRevision { get; private set; }
        public Task<bool> Ready => Task.FromResult(true);
        public event Action<SelectionActivity>? Activity;
        public void Start() { }
        public void SetEnabled(bool enabled) { Interlocked.Increment(ref epoch); EpochStartRevision = Interlocked.Increment(ref revision); }
        public SelectionActivity Create(SelectionActivityKind kind, uint key = 0, SelectionActivityModifiers modifiers = 0,
            bool completes = false, uint button = 0, nint window = default, bool baseline = false, bool injected = false)
            => new(kind, window == 0 ? 100 : window, 110, 42, VirtualKey: key, Modifiers: modifiers, MouseButton: button,
                CompletesSelectionGesture: completes, Revision: Interlocked.Increment(ref revision), Epoch: Epoch)
            { TargetRootWindow = window == 0 ? 100 : window, HasCopySequence = baseline, BeforeCopySequence = baseline ? 10u : 0u, IsInjected = injected };
        public void Deliver(SelectionActivity activity) => Activity?.Invoke(activity);
        public void Send(SelectionActivityKind kind, uint key = 0, SelectionActivityModifiers modifiers = 0,
            bool completes = false, uint button = 0, nint window = default, bool baseline = false, bool injected = false)
            => Deliver(Create(kind, key, modifiers, completes, button, window, baseline, injected));
        public void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        public Reader Reader { get; }
        public FakeActivity Activity { get; } = new();
        public ClipboardReader Clipboard { get; } = new();
        public SelectionMonitor Monitor { get; }
        public ConcurrentQueue<SelectionSnapshot> Published { get; } = new();
        private readonly Channel<SelectionSnapshot> selections = Channel.CreateUnbounded<SelectionSnapshot>();
        public Fixture(Reader? reader = null)
        {
            Reader = reader ?? new();
            Monitor = new(Reader, Activity, new() { Debounce = TimeSpan.FromDays(1), PollInterval = TimeSpan.Zero },
                copyReader: Clipboard, copyClock: () => 1000);
            Monitor.SelectionChanged += value => { Published.Enqueue(value); selections.Writer.TryWrite(value); };
        }
        public async Task Enable() { Assert.True(await Monitor.Ready.WaitAsync(TimeSpan.FromSeconds(2))); Monitor.SetEnabled(true); }
        public void Gesture()
        { Activity.Send(SelectionActivityKind.PointerDown, button: 1); Activity.Send(SelectionActivityKind.PointerUp, completes: true, button: 1); }
        public void Copy(nint window = default, bool baseline = true, bool injected = false)
        {
            Activity.Send(SelectionActivityKind.KeyDown, 0x11, SelectionActivityModifiers.Control, window: window);
            Activity.Send(SelectionActivityKind.KeyDown, 0x43, SelectionActivityModifiers.Control, window: window, baseline: baseline, injected: injected);
            Activity.Send(SelectionActivityKind.KeyUp, 0x43, SelectionActivityModifiers.Control, window: window);
            Activity.Send(SelectionActivityKind.KeyUp, 0x11, window: window);
        }
        public Task<SelectionSnapshot> Next() => selections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public void Dispose() => Monitor.Dispose();
    }
}
