using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// Classification and an injected metadata source only. Start never reaches the
// native source: no hooks, windows, UIA, input or clipboard APIs are invoked.
[Trait("Category", "HeadlessOnly")]
public sealed class SelectionActivityMonitorTests
{
    [ThreadStatic] private static bool _insideSourceRaise;

    [Fact]
    public async Task ConstructionDoesNotStartTheInjectedSourceAndDisposalIsIdempotent()
    {
        var source = new FakeSource();
        var monitor = new SelectionActivityMonitor(source, ownProcessId: 42);
        Assert.Equal(0, source.Starts); Assert.False(monitor.Ready.IsCompleted);
        monitor.Dispose(); monitor.Stop(); monitor.Start();
        Assert.Equal(0, source.Starts); Assert.Equal(1, source.Disposals);
        Assert.False(await monitor.Ready);
    }

    [Fact]
    public async Task StartIsExplicitAndSourceFailureIsReportedWithoutInstallingAnything()
    {
        var source = new FakeSource { StartSucceeds = false };
        using var monitor = new SelectionActivityMonitor(source, 42);
        monitor.Start(); monitor.Start();
        Assert.False(await monitor.Ready.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, source.Starts);
    }

    [Fact]
    public void OwnPointerTargetIsIgnoredEvenWhenForegroundBelongsToTheExternalEditor()
    {
        var classifier = new SelectionActivityClassifier(42);
        Assert.Null(classifier.Pointer(SelectionActivityKind.PointerDown, 10, 30, 42, 5, 6, 1, 0, 0, 30));
        Assert.Null(classifier.Pointer(SelectionActivityKind.PointerUp, 10, 30, 42, 5, 6, 1, 0, 0, 30));
    }

    [Fact]
    public void PointerCompletionMatchesTheActualTargetRootInsteadOfAChildWindow()
    {
        var classifier = new SelectionActivityClassifier(42);
        var down = classifier.Pointer(SelectionActivityKind.PointerDown, 10, 21, 51, -100, 20, 1, 0, 0, 20)!.Value;
        var up = classifier.Pointer(SelectionActivityKind.PointerUp, 20, 22, 51, -80, 30, 1, 0, 0, 20)!.Value;
        Assert.False(down.CompletesSelectionGesture); Assert.True(up.CompletesSelectionGesture);
        Assert.Equal((nint)22, up.WindowUnderPointer); Assert.Equal((nint)20, up.TargetWindow);
        _ = classifier.Pointer(SelectionActivityKind.PointerDown, 20, 21, 51, 0, 0, 1, 0, 0, 20);
        var otherRoot = classifier.Pointer(SelectionActivityKind.PointerUp, 40, 41, 51, 0, 0, 1, 0, 0, 40)!.Value;
        Assert.False(otherRoot.CompletesSelectionGesture);
    }

    [Fact]
    public void OwnMarkedSyntheticInputDoesNotAlterPhysicalModifierOrPointerState()
    {
        var classifier = new SelectionActivityClassifier(42);
        nuint marker = SelectionActivityClassifier.SyntheticInputMarker;
        Assert.Null(classifier.Keyboard(0x11, true, 0, 0x10, marker, 10, 51));
        Assert.Equal(SelectionActivityModifiers.None, classifier.Modifiers);
        _ = classifier.Pointer(SelectionActivityKind.PointerDown, 10, 20, 51, 0, 0, 1, 0, 0);
        Assert.Null(classifier.Pointer(SelectionActivityKind.PointerUp, 10, 20, 51, 0, 0, 1, 0, marker));
        Assert.True(classifier.Pointer(SelectionActivityKind.PointerUp, 10, 20, 51, 0, 0, 1, 0, 0)!.Value.CompletesSelectionGesture);
    }

    [Fact]
    public void ForeignInjectedKeysAreObservedAndShiftReleaseBeforeArrowUpStillCompletesSelection()
    {
        var classifier = new SelectionActivityClassifier(42);
        Assert.NotNull(classifier.Keyboard(0x10, true, 0x36, 0x10, 0, 10, 51));
        var down = classifier.Keyboard(0x25, true, 0, 0x10, 0, 10, 51)!.Value;
        Assert.Equal(SelectionActivityModifiers.Shift, down.Modifiers);
        _ = classifier.Keyboard(0x10, false, 0x36, 0x10, 0, 10, 51);
        var up = classifier.Keyboard(0x25, false, 0, 0x10, 0, 10, 51)!.Value;
        Assert.Equal(SelectionActivityModifiers.None, up.Modifiers);
        Assert.True(up.CompletesSelectionGesture);
    }

    [Fact]
    public void ControlACompletionIsRememberedWhenControlIsReleasedFirst()
    {
        var classifier = new SelectionActivityClassifier(42);
        _ = classifier.Keyboard(0x11, true, 0x1D, 1, 0, 10, 51);
        _ = classifier.Keyboard(0x41, true, 0, 0, 0, 10, 51);
        Assert.True(classifier.Keyboard(0x41, true, 0, 0, 0, 10, 51)!.Value.IsRepeat);
        _ = classifier.Keyboard(0x11, false, 0x1D, 1, 0, 10, 51);
        var up = classifier.Keyboard(0x41, false, 0, 0, 0, 10, 51)!.Value;
        Assert.True(up.CompletesSelectionGesture); Assert.Equal(SelectionActivityModifiers.None, up.Modifiers);
    }

    [Fact]
    public void ModifierTransitionsOverOwnWindowAreTrackedButNotPublished()
    {
        var classifier = new SelectionActivityClassifier(42);
        Assert.Null(classifier.Keyboard(0x11, true, 0x1D, 0, 0, 30, 42));
        Assert.Equal(SelectionActivityModifiers.Control, classifier.Modifiers);
        var released = classifier.Keyboard(0x11, false, 0x1D, 0, 0, 10, 51)!.Value;
        Assert.Equal(SelectionActivityModifiers.None, released.Modifiers);
        Assert.False(released.CompletesSelectionGesture);
    }

    [Fact]
    public void WheelRetainsPhysicalPointAndDeltaWithoutPretendingToCompleteASelection()
    {
        var activity = new SelectionActivityClassifier(42).Pointer(SelectionActivityKind.PointerWheel, 10, 21, 51, -300, 900, 0, -120, 0, 20)!.Value;
        Assert.Equal(-300, activity.X); Assert.Equal(900, activity.Y); Assert.Equal(-120, activity.WheelDelta);
        Assert.False(activity.CompletesSelectionGesture); Assert.Equal((nint)20, activity.TargetWindow);
    }

    [Fact]
    public async Task SubscriberDispatchIsNotInlineOnTheSourceCallbackStack()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        var observed = Completion<bool>();
        monitor.Activity += _ => observed.TrySetResult(_insideSourceRaise);
        monitor.SetEnabled(true); monitor.Start();
        Assert.True(await monitor.Ready);
        source.Raise(Key(0x41));
        Assert.False(await observed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task RevisionAdvancesBeforeABlockedSubscriberCanProcessTheNextEvent()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        using var release = new ManualResetEventSlim();
        var entered = Completion<bool>(); var second = Completion<SelectionActivity>();
        monitor.Activity += activity =>
        {
            if (activity.VirtualKey == 0x41) { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); }
            if (activity.VirtualKey == 0x42) second.TrySetResult(activity);
        };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        long modeStart = monitor.EpochStartRevision;
        try
        {
            source.Raise(Key(0x41)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            long old = monitor.Revision;
            source.Raise(Key(0x42));
            Assert.True(monitor.Revision > old); Assert.False(second.Task.IsCompleted);
            // A mode baseline must not adopt a physical activity that is still
            // queued; otherwise a reader can mark that unhandled revision current.
            Assert.Equal(modeStart, monitor.EpochStartRevision);
            Assert.True(modeStart < monitor.Revision);
            release.Set();
            Assert.Equal(monitor.Revision, (await second.Task.WaitAsync(TimeSpan.FromSeconds(3))).Revision);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OwnResultCopyClickDoesNotAdvanceRevisionOrReachSubscribers()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        var received = new List<SelectionActivity>(); var done = Completion<bool>();
        monitor.Activity += activity => { received.Add(activity); if (activity.VirtualKey == 0x43) done.TrySetResult(true); };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        long before = monitor.Revision;
        source.Raise(new(SelectionActivityKind.PointerDown, 10, 30, 42, MouseButton: 1));
        source.Raise(new(SelectionActivityKind.PointerUp, 10, 30, 42, MouseButton: 1, CompletesSelectionGesture: true));
        Assert.Equal(before, monitor.Revision);
        source.Raise(Key(0x43)); await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(received); Assert.Equal(before + 1, monitor.Revision);
    }

    [Fact]
    public async Task QueuedOldEpochActivityCannotReachTheNewEnabledSession()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        using var release = new ManualResetEventSlim();
        var entered = Completion<bool>(); var done = Completion<bool>();
        var received = new List<uint>();
        monitor.Activity += activity =>
        {
            received.Add(activity.VirtualKey);
            if (activity.VirtualKey == 0x41) { entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(3)); }
            if (activity.VirtualKey == 0x43) done.TrySetResult(true);
        };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        try
        {
            source.Raise(Key(0x41)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            source.Raise(Key(0x42));
            monitor.SetEnabled(false); monitor.SetEnabled(true);
            source.Raise(Key(0x43)); release.Set();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new uint[] { 0x41, 0x43 }, received);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CallbackWhichCapturedAnOldEpochIsNotRestampedAsFresh()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        var received = new List<uint>(); var done = Completion<bool>();
        monitor.Activity += activity => { received.Add(activity.VirtualKey); done.TrySetResult(true); };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        long oldEpoch = monitor.Epoch;
        monitor.SetEnabled(false); monitor.SetEnabled(true);
        long before = monitor.Revision;
        source.Raise(Key(0x41) with { Epoch = oldEpoch, HasCapturedEpoch = true });
        Assert.Equal(before, monitor.Revision);
        source.Raise(Key(0x43) with { Epoch = monitor.Epoch, HasCapturedEpoch = true });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new uint[] { 0x43 }, received);
    }

    [Fact]
    public async Task DisabledActivityCannotCreateAnUndeliverableRevisionInTheNextMode()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        var done = Completion<SelectionActivity>();
        monitor.Activity += activity => done.TrySetResult(activity);
        monitor.Start(); await monitor.Ready;
        monitor.SetEnabled(false);
        long inactiveRevision = monitor.Revision;
        source.Raise(Key(0x41));
        source.Raise(new(SelectionActivityKind.PointerDown, 10, 20, 51, MouseButton: 1));
        Assert.Equal(inactiveRevision, monitor.Revision);
        Assert.False(done.Task.IsCompleted);

        monitor.SetEnabled(true);
        long modeStart = monitor.EpochStartRevision;
        Assert.Equal(inactiveRevision + 1, modeStart);
        source.Raise(Key(0x42));
        var accepted = await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0x42u, accepted.VirtualKey);
        Assert.Equal(modeStart + 1, accepted.Revision);
        Assert.Equal(modeStart, monitor.EpochStartRevision);
    }

    [Fact]
    public async Task PointerUpFromADragStartedBeforePauseCannotUnlockFreshSelection()
    {
        var source = new FakeSource(); var classifier = new SelectionActivityClassifier(42);
        using var monitor = new SelectionActivityMonitor(source, 42);
        var stale = Completion<SelectionActivity>(); var fresh = Completion<SelectionActivity>();
        monitor.Activity += activity =>
        {
            if (activity.Kind != SelectionActivityKind.PointerUp) return;
            if (activity.CompletesSelectionGesture) fresh.TrySetResult(activity); else stale.TrySetResult(activity);
        };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        source.Raise(classifier.Pointer(SelectionActivityKind.PointerDown, 10, 21, 51, 1, 2, 1, 0, 0, 20)!.Value);
        monitor.SetEnabled(false); monitor.SetEnabled(true);
        source.Raise(classifier.Pointer(SelectionActivityKind.PointerUp, 20, 22, 51, 5, 6, 1, 0, 0, 20)!.Value);
        Assert.False((await stale.Task.WaitAsync(TimeSpan.FromSeconds(3))).CompletesSelectionGesture);
        source.Raise(classifier.Pointer(SelectionActivityKind.PointerDown, 20, 21, 51, 1, 2, 1, 0, 0, 20)!.Value);
        source.Raise(classifier.Pointer(SelectionActivityKind.PointerUp, 20, 22, 51, 5, 6, 1, 0, 0, 20)!.Value);
        Assert.True((await fresh.Task.WaitAsync(TimeSpan.FromSeconds(3))).CompletesSelectionGesture);
    }

    [Fact]
    public async Task ASelectionKeyHeldAcrossPauseCannotUnlockTheNewEpochOnRelease()
    {
        var source = new FakeSource(); var classifier = new SelectionActivityClassifier(42);
        using var monitor = new SelectionActivityMonitor(source, 42);
        var done = Completion<SelectionActivity>();
        monitor.Activity += activity => { if (activity.Kind == SelectionActivityKind.KeyUp && activity.VirtualKey == 0x41) done.TrySetResult(activity); };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        source.Raise(classifier.Keyboard(0x11, true, 0x1D, 0, 0, 10, 51)!.Value);
        source.Raise(classifier.Keyboard(0x41, true, 0, 0, 0, 10, 51)!.Value);
        monitor.SetEnabled(false); monitor.SetEnabled(true);
        source.Raise(classifier.Keyboard(0x11, false, 0x1D, 0, 0, 10, 51)!.Value);
        source.Raise(classifier.Keyboard(0x41, false, 0, 0, 0, 10, 51)!.Value);
        Assert.False((await done.Task.WaitAsync(TimeSpan.FromSeconds(3))).CompletesSelectionGesture);
    }

    [Fact]
    public async Task CompletionSurvivesLaterModifierUpRevisionInTheSameEpoch()
    {
        var source = new FakeSource(); var classifier = new SelectionActivityClassifier(42);
        using var monitor = new SelectionActivityMonitor(source, 42);
        var done = Completion<SelectionActivity>();
        monitor.Activity += activity => { if (activity.Kind == SelectionActivityKind.KeyUp && activity.VirtualKey == 0x41) done.TrySetResult(activity); };
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        source.Raise(classifier.Keyboard(0x11, true, 0x1D, 0, 0, 10, 51)!.Value);
        source.Raise(classifier.Keyboard(0x41, true, 0, 0, 0, 10, 51)!.Value);
        source.Raise(classifier.Keyboard(0x41, false, 0, 0, 0, 10, 51)!.Value);
        source.Raise(classifier.Keyboard(0x11, false, 0x1D, 0, 0, 10, 51)!.Value);
        var completion = await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(completion.CompletesSelectionGesture); Assert.True(completion.Revision < monitor.Revision);
    }

    [Fact]
    public async Task ThrowingSubscriberCannotStopOtherSubscribersOrFutureActivity()
    {
        var source = new FakeSource();
        using var monitor = new SelectionActivityMonitor(source, 42);
        var done = Completion<SelectionActivity>();
        monitor.Activity += _ => throw new InvalidOperationException("subscriber");
        monitor.Activity += activity => done.TrySetResult(activity);
        monitor.SetEnabled(true); monitor.Start(); await monitor.Ready;
        source.Raise(Key(0x43));
        Assert.Equal(0x43u, (await done.Task.WaitAsync(TimeSpan.FromSeconds(3))).VirtualKey);
    }

    private static SelectionActivity Key(uint key) => new(SelectionActivityKind.KeyDown, 10, 0, 51, VirtualKey: key);
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class FakeSource : ISelectionActivitySource
    {
        private readonly TaskCompletionSource<bool> _ready = Completion<bool>();
        public Task<bool> Ready => _ready.Task;
        public bool StartSucceeds = true;
        public int Starts, Disposals;
        public event Action<SelectionActivity>? Activity;
        public void Start() { Starts++; _ready.TrySetResult(StartSucceeds); }
        public void Raise(SelectionActivity activity)
        {
            _insideSourceRaise = true;
            try { Activity?.Invoke(activity); } finally { _insideSourceRaise = false; }
        }
        public void Dispose() { Disposals++; _ready.TrySetResult(false); }
    }
}
