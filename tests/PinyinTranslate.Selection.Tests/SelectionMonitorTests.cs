using System.Collections.Concurrent;
using System.Threading.Channels;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

/// <summary>Only injected managed readers/activity sources; no production native constructor is called.</summary>
public sealed class SelectionMonitorTests
{
    [Fact]
    public async Task DuplicateEventsAndPollsPreserveTheCurrentGenerationAndValidation()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("paragraph", "range-1");
        await fixture.EnableAndRead();
        var first = await fixture.Next();
        for (int i = 0; i < 8; i++)
        {
            fixture.Reader.Signal(SelectionReaderChangeKind.Selection, verified: true);
            await fixture.Monitor.RequestReadAsync();
            Assert.True(fixture.Monitor.IsCurrent(first.Version));
        }
        Assert.True(await fixture.Monitor.ValidateAsync(first));
        Assert.Single(fixture.Published);
    }

    [Fact]
    public async Task MissingProviderEventIsRecoveredByAPollAndSameTextAtAnotherRangeInvalidates()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("same word", "position-1");
        await fixture.EnableAndRead();
        var first = await fixture.Next();
        fixture.Reader.Result = Result("same word", "position-2");
        await fixture.Monitor.RequestReadAsync();
        var second = await fixture.Next();
        Assert.NotEqual(first.Version, second.Version);
        Assert.False(fixture.Monitor.IsCurrent(first.Version));
        Assert.False(await fixture.Monitor.ValidateAsync(first));
        Assert.True(await fixture.Monitor.ValidateAsync(second));
    }

    [Fact]
    public async Task RangeAbaUsesADifferentGenerationAndNeverRevivesTheOldSnapshot()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("A", "A");
        await fixture.EnableAndRead();
        var first = await fixture.Next();
        fixture.Reader.Result = Result("B", "B");
        await fixture.Monitor.RequestReadAsync();
        await fixture.Next();
        fixture.Reader.Result = Result("A", "A");
        await fixture.Monitor.RequestReadAsync();
        var last = await fixture.Next();
        Assert.Equal(first.Text, last.Text);
        Assert.NotEqual(first.Version, last.Version);
        Assert.False(await fixture.Monitor.ValidateAsync(first));
    }

    [Fact]
    public async Task ResumeCapturesBaselineAndDuplicateOldEventsCannotUnlockIt()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("old selected paragraph", "old") with { FromForegroundEvent = true };
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.False(fixture.Monitor.IsCurrent(1));
        for (int i = 0; i < 3; i++)
        {
            fixture.Reader.Signal(SelectionReaderChangeKind.Selection, verified: true);
            await fixture.Monitor.RequestReadAsync();
            Assert.False(fixture.Monitor.IsCurrent(1));
        }
        fixture.Reader.Result = Result("new selection", "new");
        await fixture.Monitor.RequestReadAsync();
        var current = await fixture.Next();
        Assert.Equal("new selection", current.Text);
    }

    [Fact]
    public async Task MatchingCompletedGestureCanPublishEvenWhenSelectingTheSameRangeAgain()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("paragraph", "same");
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Activity.Send(SelectionActivityKind.PointerDown);
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("paragraph", (await fixture.Next()).Text);
        Assert.Equal(SelectionReadOrigin.Pointer, fixture.Reader.Requests.Last().Origin);
    }

    [Fact]
    public async Task UnmatchedUpFromBeforePauseDoesNotUnlockTheFreshBarrier()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("old selection", "old");
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: false);
        await fixture.Monitor.RequestReadAsync();
        // Epoch starts at one, then activity invalidation advances selection version to two.
        Assert.False(fixture.Monitor.IsCurrent(2));
    }

    [Fact]
    public async Task PointerDragAndKeyboardSelectionAreNotPublishedBeforeTheirCompletion()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("initial", "initial");
        await fixture.EnableAndRead();
        var initial = await fixture.Next();
        fixture.Activity.Send(SelectionActivityKind.PointerDown);
        int reads = fixture.Reader.ReadCount;
        fixture.Reader.Result = Result("half dragged", "half");
        fixture.Reader.Signal(SelectionReaderChangeKind.Selection, verified: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount);
        Assert.False(fixture.Monitor.IsCurrent(initial.Version));
        fixture.Reader.Result = Result("finished drag", "final");
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("finished drag", (await fixture.Next()).Text);
        fixture.Activity.Send(SelectionActivityKind.KeyDown, key: 0x27, modifiers: SelectionActivityModifiers.Shift);
        reads = fixture.Reader.ReadCount;
        fixture.Reader.Result = Result("keyboard in progress", "key-half");
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount);
        fixture.Activity.Send(SelectionActivityKind.KeyUp, completes: true, key: 0x27);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("keyboard in progress", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task ActivityRevisionRevokesBeforeDelayedDispatchAndCompletionMayHaveAnOlderRevision()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("original", "original");
        await fixture.EnableAndRead();
        var first = await fixture.Next();
        var completion = fixture.Activity.Create(SelectionActivityKind.KeyUp, completes: true, key: 0x41);
        var ctrlUp = fixture.Activity.Create(SelectionActivityKind.KeyUp, key: 0x11); // Already captured before queued A-up arrives.
        Assert.False(fixture.Monitor.IsCurrent(first.Version));
        Assert.False(await fixture.Monitor.ValidateAsync(first));
        fixture.Reader.Result = Result("Ctrl A selection", "all");
        fixture.Activity.Deliver(completion);
        int reads = fixture.Reader.ReadCount;
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount); // Newer Ctrl-up has not yet reached the monitor.
        fixture.Activity.Deliver(ctrlUp);
        await fixture.Monitor.RequestReadAsync();
        var second = await fixture.Next();
        Assert.Equal(fixture.Activity.Revision, second.ActivityRevision);
        Assert.True(fixture.Monitor.IsCurrent(second.Version));
    }

    [Fact]
    public async Task DisableEnableAndOldEpochEventsCannotReviveTheOldSelection()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("old", "old");
        await fixture.EnableAndRead();
        var old = await fixture.Next();
        long oldEpoch = fixture.Reader.Epoch;
        var oldActivity = fixture.Activity.Create(SelectionActivityKind.PointerUp, completes: true);
        fixture.Monitor.SetEnabled(false);
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Reader.Emit(new(oldEpoch, SelectionReaderChangeKind.Selection, true));
        fixture.Activity.Deliver(oldActivity);
        await fixture.Monitor.RequestReadAsync();
        Assert.False(fixture.Monitor.IsCurrent(old.Version));
        fixture.Reader.Result = Result("fresh", "fresh");
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("fresh", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task ActivityDuringABlockedReadRejectsTheLateResult()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reader.ReadOverride = _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Result("late original", "old");
        };
        fixture.Monitor.SetEnabled(true);
        var read = fixture.Monitor.RequestReadAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Activity.Send(SelectionActivityKind.KeyDown, key: 0x42);
        release.TrySetResult();
        await read;
        fixture.Reader.ReadOverride = null;
        fixture.Reader.Result = Result("current", "new");
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("current", (await fixture.Next()).Text);
        Assert.DoesNotContain(fixture.Published, selection => selection.Text == "late original");
    }

    [Fact]
    public async Task FinalRangeValidationFailureRevokesCurrentAndSubscriberExceptionsDoNotKillTheWorker()
    {
        using var fixture = new Fixture();
        fixture.Monitor.SelectionChanged += _ => throw new InvalidOperationException("consumer");
        fixture.Reader.Result = Result("old", "old");
        await fixture.EnableAndRead();
        var first = await fixture.Next();
        fixture.Reader.Validation = false;
        Assert.False(await fixture.Monitor.ValidateAsync(first));
        Assert.False(fixture.Monitor.IsCurrent(first.Version));
        fixture.Reader.Validation = true;
        fixture.Reader.Result = Result("new", "new");
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("new", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task EventStormDoesNotPostponeTheFirstScheduledReadIndefinitely()
    {
        var reader = new Reader { Result = Result("foreground document", "range") };
        using var monitor = new SelectionMonitor(reader, options: new()
            { Debounce = TimeSpan.FromMilliseconds(20), PollInterval = TimeSpan.Zero });
        var published = new TaskCompletionSource<SelectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SelectionChanged += selection => published.TrySetResult(selection);
        await monitor.Ready;
        monitor.SetEnabled(true);
        using var stop = new CancellationTokenSource();
        var storm = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                reader.Signal(SelectionReaderChangeKind.Selection, verified: false);
                await Task.Delay(2);
            }
        });
        try { Assert.Equal("foreground document", (await published.Task.WaitAsync(TimeSpan.FromSeconds(2))).Text); }
        finally { stop.Cancel(); await storm; }
    }

    [Fact]
    public async Task EmptyCompletedGestureReportsOnceAndOrdinaryPollsDoNotRefreshTheNotice()
    {
        using var fixture = new Fixture();
        var reported = new ConcurrentQueue<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Monitor.StatusChanged += message =>
        {
            if (!message.StartsWith("未读取到", StringComparison.Ordinal)) return;
            reported.Enqueue(message); first.TrySetResult();
        };
        await fixture.EnableAndRead(); // Ordinary empty enable read is not an error notice.
        Assert.Empty(reported);
        fixture.Activity.Send(SelectionActivityKind.PointerDown);
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 4; i++) await fixture.Monitor.RequestReadAsync();
        fixture.Activity.Send(SelectionActivityKind.PointerDown);
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Single(reported);
        Assert.Contains("如已划选", reported.Single());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("runtime")]
    [InlineData("pid")]
    public async Task ProviderOrWindowIdentityChangesOnlyRefreshThePausedBaseline(string identity)
    {
        using var fixture = new Fixture();
        var original = Result("old selection", "old");
        fixture.Reader.Result = original;
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Reader.Result = identity switch
        {
            "scope" => original with { ScopeId = "another-scope", WindowHandle = 200 },
            "runtime" => original with { RuntimeId = "rebuilt-provider" },
            _ => original with { ProcessId = 43 }
        };
        await fixture.Monitor.RequestReadAsync();
        Assert.False(fixture.Monitor.IsCurrent(1));
        fixture.Reader.Result = fixture.Reader.Result! with { Text = "real new range", RangeId = "new-location" };
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("real new range", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task ASelectionReappearingAfterAnEmptyPausedBaselineMustFirstBecomeTheNewBaseline()
    {
        using var fixture = new Fixture();
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Reader.Result = Result("old selected text reappeared", "old");
        await fixture.Monitor.RequestReadAsync();
        Assert.False(fixture.Monitor.IsCurrent(1));
        fixture.Reader.Result = Result("actual newly selected text", "new");
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("actual newly selected text", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task UnmatchedNavigationUpAfterModifiersAreReleasedEndsOnlyKeyboardSuppression()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("old", "old");
        fixture.Monitor.SetEnabled(true, requireFreshSelection: true);
        await fixture.Monitor.RequestReadAsync();
        fixture.Activity.Send(SelectionActivityKind.KeyDown, key: 0x27, modifiers: SelectionActivityModifiers.Shift);
        fixture.Activity.Send(SelectionActivityKind.KeyUp, key: 0x10);
        fixture.Activity.Send(SelectionActivityKind.KeyUp, key: 0x27, completes: false);
        int reads = fixture.Reader.ReadCount;
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads + 1, fixture.Reader.ReadCount);
        Assert.False(fixture.Monitor.IsCurrent(4)); // Unmatched up is not proof of a fresh selection.
        fixture.Activity.Send(SelectionActivityKind.PointerDown);
        fixture.Activity.Send(SelectionActivityKind.KeyUp, key: 0x27, completes: false);
        reads = fixture.Reader.ReadCount;
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount); // Pointer drag is still held.
        fixture.Reader.Result = Result("fresh drag", "fresh");
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("fresh drag", (await fixture.Next()).Text);
    }

    [Fact]
    public async Task UndeliveredActivityRevisionCannotBeStampedOntoAnOldOrHalfDraggedRange()
    {
        using var fixture = new Fixture();
        fixture.Reader.Result = Result("before drag", "old");
        await fixture.EnableAndRead();
        var old = await fixture.Next();
        var pendingDown = fixture.Activity.Create(SelectionActivityKind.PointerDown);
        fixture.Reader.Result = Result("half dragged", "half");
        int reads = fixture.Reader.ReadCount;
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount);
        Assert.False(fixture.Monitor.IsCurrent(old.Version));
        fixture.Activity.Deliver(pendingDown);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(reads, fixture.Reader.ReadCount);
        fixture.Reader.Result = Result("finished drag", "final");
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("finished drag", (await fixture.Next()).Text);
        Assert.DoesNotContain(fixture.Published, value => value.Text == "half dragged");
    }

    [Fact]
    public async Task PendingInputAfterModeTransitionCannotBecomeTheModesHandledBaseline()
    {
        using var fixture = new Fixture();
        SelectionActivity? pending = null;
        fixture.Activity.AfterSetEnabled = () => pending = fixture.Activity.Create(SelectionActivityKind.PointerDown);
        fixture.Reader.Result = Result("old selection", "old");
        fixture.Monitor.SetEnabled(true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal(0, fixture.Reader.ReadCount);
        Assert.NotNull(pending);
        fixture.Activity.Deliver(pending.GetValueOrDefault());
        fixture.Activity.Send(SelectionActivityKind.PointerUp, completes: true);
        await fixture.Monitor.RequestReadAsync();
        Assert.Equal("old selection", (await fixture.Next()).Text);
    }

    private static SelectionReadResult Result(string text, string range) => new(42, 100, "document", text, range, "100");
    private sealed class Fixture : IDisposable
    {
        private readonly Channel<SelectionSnapshot> selections = Channel.CreateUnbounded<SelectionSnapshot>();
        public readonly Reader Reader = new();
        public readonly FakeActivity Activity = new();
        public readonly SelectionMonitor Monitor;
        public readonly ConcurrentQueue<SelectionSnapshot> Published = new();
        public Fixture()
        {
            Monitor = new(Reader, Activity, new() { Debounce = TimeSpan.FromDays(1), PollInterval = TimeSpan.Zero });
            Monitor.SelectionChanged += selection => { Published.Enqueue(selection); selections.Writer.TryWrite(selection); };
        }
        public async Task EnableAndRead()
        {
            Assert.True(await Monitor.Ready.WaitAsync(TimeSpan.FromSeconds(2)));
            Monitor.SetEnabled(true);
            await Monitor.RequestReadAsync();
        }
        public Task<SelectionSnapshot> Next() => selections.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        public void Dispose() => Monitor.Dispose();
    }
    private sealed class Reader : ISelectionReader
    {
        public SelectionReadResult? Result;
        public Func<SelectionReadRequest, SelectionReadResult?>? ReadOverride;
        public bool Validation = true;
        public long Epoch;
        public int ReadCount;
        public readonly ConcurrentQueue<SelectionReadRequest> Requests = new();
        public event Action<SelectionReaderSignal>? Changed;
        public bool Start() => true;
        public void SetEnabled(bool enabled, long epoch) => Epoch = epoch;
        public SelectionReadResult? Read(SelectionReadRequest request)
        {
            Interlocked.Increment(ref ReadCount); Requests.Enqueue(request);
            return ReadOverride is { } read ? read(request) : Result;
        }
        public bool Validate(SelectionReadResult selection) => Validation && selection == Result;
        public void Signal(SelectionReaderChangeKind kind, bool verified = false) => Emit(new(Epoch, kind, verified));
        public void Emit(SelectionReaderSignal signal) => Changed?.Invoke(signal);
        public void Dispose() { }
    }
    private sealed class FakeActivity : ISelectionActivityMonitor
    {
        private long revision, epoch, epochStartRevision;
        public Task<bool> Ready => Task.FromResult(true);
        public long Revision => Interlocked.Read(ref revision);
        public long Epoch => Interlocked.Read(ref epoch);
        public long EpochStartRevision => Interlocked.Read(ref epochStartRevision);
        public Action? AfterSetEnabled;
        public event Action<SelectionActivity>? Activity;
        public void Start() { }
        public void SetEnabled(bool enabled)
        {
            Interlocked.Increment(ref epoch);
            Interlocked.Exchange(ref epochStartRevision, Interlocked.Increment(ref revision));
            AfterSetEnabled?.Invoke();
        }
        public void AdvanceRevision() => Interlocked.Increment(ref revision);
        public SelectionActivity Create(SelectionActivityKind kind, bool completes = false, uint key = 0,
            SelectionActivityModifiers modifiers = SelectionActivityModifiers.None) => new(kind, 100, 110, 42,
                X: 15, Y: 20, VirtualKey: key, Modifiers: modifiers, CompletesSelectionGesture: completes,
                Revision: Interlocked.Increment(ref revision), Epoch: Epoch) { TargetRootWindow = 100 };
        public void Send(SelectionActivityKind kind, bool completes = false, uint key = 0,
            SelectionActivityModifiers modifiers = SelectionActivityModifiers.None) => Deliver(Create(kind, completes, key, modifiers));
        public void Deliver(SelectionActivity change) => Activity?.Invoke(change);
        public void Dispose() { }
    }
}
