using System.Collections.Concurrent;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

public sealed class CandidateTranslationCoordinatorTests
{
    [ThreadStatic] private static bool _insideHook;

    private static CandidateObservation Snapshot(long version, string candidate = "甲", IReadOnlyList<string>? page = null) =>
        new(version, 123, 456, "focus", candidate, page ?? [candidate, "其他"], new(100, 200, 300, 40));

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<CandidateTranslationState> WaitForStateAsync(
        CandidateTranslationCoordinator coordinator, Func<CandidateTranslationState, bool> predicate)
    {
        var completion = Completion<CandidateTranslationState>();
        void Changed(CandidateTranslationState state) { if (predicate(state)) completion.TrySetResult(state); }
        coordinator.Changed += Changed;
        try
        {
            if (predicate(coordinator.State)) return coordinator.State;
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { coordinator.Changed -= Changed; }
    }

    [Fact]
    public async Task PreviewWorksWithoutAcceptorAndCopiesTheVisiblePage()
    {
        var translation = Completion<string>();
        var started = Completion<CandidateObservation>();
        using var coordinator = new CandidateTranslationCoordinator((observation, _) =>
        {
            started.TrySetResult(observation);
            return translation.Task;
        }, (_, _) => Task.FromResult(true));
        var page = new List<string> { "甲", "乙" };
        coordinator.SetEnabled(true);
        Assert.True(coordinator.Observe(Snapshot(1, page: page)));
        var captured = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page[0] = "被外部修改";
        translation.SetResult("first");
        var state = await WaitForStateAsync(coordinator, x => x.Translation == "first");
        Assert.Equal("甲", captured.VisiblePage[0]);
        Assert.Equal("甲", state.Candidate);
        Assert.Equal(new CandidateBounds(100, 200, 300, 40), state.Bounds);
        Assert.False(state.IsWaiting);
        Assert.False(state.CanRequestAccept);
        Assert.False(coordinator.HasAcceptor);
        Assert.False(coordinator.TryRequestAccept());
    }

    [Fact]
    public async Task LateAbaTranslationsCannotReplaceTheNewestA()
    {
        var translations = Enumerable.Range(1, 3).ToDictionary(x => (long)x, _ => Completion<string>());
        var started = Enumerable.Range(1, 3).ToDictionary(x => (long)x, _ => Completion<bool>());
        var canceled = Enumerable.Range(1, 2).ToDictionary(x => (long)x, _ => Completion<bool>());
        using var coordinator = new CandidateTranslationCoordinator((observation, token) =>
        {
            if (canceled.TryGetValue(observation.Version, out var cancellation)) token.Register(() => cancellation.TrySetResult(true));
            started[observation.Version].TrySetResult(true);
            return translations[observation.Version].Task; // Deliberately ignores cancellation.
        }, (_, _) => Task.FromResult(true));
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        long firstGeneration = coordinator.State.Generation;
        coordinator.Observe(Snapshot(2, "乙"));
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Observe(Snapshot(3));
        await started[3].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(canceled.Values.Select(x => x.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        translations[3].SetResult("new A");
        var current = await WaitForStateAsync(coordinator, x => x.Translation == "new A");
        translations[2].SetResult("late B");
        translations[1].SetResult("late A");
        Assert.False(coordinator.Observe(Snapshot(1)));
        Assert.Equal(3, current.Observation!.Version);
        Assert.True(current.Generation > firstGeneration);
        Assert.Equal("new A", coordinator.State.Translation);
    }

    [Fact]
    public async Task DisableAndReenableRequireANewSourceObservation()
    {
        var oldTranslation = Completion<string>();
        var started = Completion<bool>();
        var canceled = Completion<bool>();
        using var coordinator = new CandidateTranslationCoordinator((observation, token) =>
        {
            if (observation.Version != 1) return Task.FromResult("new epoch");
            token.Register(() => canceled.TrySetResult(true));
            started.TrySetResult(true);
            return oldTranslation.Task;
        }, (_, _) => Task.FromResult(true));
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long oldGeneration = coordinator.State.Generation;
        coordinator.SetEnabled(false);
        coordinator.SetEnabled(true);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(coordinator.State.Observation);
        Assert.False(coordinator.Observe(Snapshot(1)));
        Assert.True(coordinator.Observe(Snapshot(2)));
        var state = await WaitForStateAsync(coordinator, x => x.Translation == "new epoch");
        oldTranslation.SetResult("old epoch");
        Assert.True(state.Generation > oldGeneration);
        Assert.Equal("new epoch", coordinator.State.Translation);
    }

    [Fact]
    public async Task TabBeforeTranslationWaitsAndRepeatedTabsWriteExactlyOnce()
    {
        var translation = Completion<string>();
        var started = Completion<bool>();
        var accepting = Completion<string>();
        var acceptFinished = Completion<bool>();
        int writes = 0, validations = 0;
        using var coordinator = new CandidateTranslationCoordinator((_, _) =>
        {
            started.TrySetResult(true);
            return translation.Task;
        }, (_, _) =>
        {
            Interlocked.Increment(ref validations);
            return Task.FromResult(true);
        }, async (_, text, token) =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref writes);
            accepting.TrySetResult(text);
            return await acceptFinished.Task.WaitAsync(token);
        });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.TryRequestAccept());
        Assert.True(coordinator.TryRequestAccept());
        Assert.True(coordinator.State.AcceptRequested);
        Assert.True(coordinator.State.IsWaiting);
        Assert.Equal(0, Volatile.Read(ref writes));
        translation.SetResult("English");
        Assert.Equal("English", await accepting.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(coordinator.TryRequestAccept());
        acceptFinished.SetResult(true);
        await WaitForStateAsync(coordinator, x => x.Observation is null && x.Generation > 2);
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Equal(3, Volatile.Read(ref validations));
        Assert.False(coordinator.TryRequestAccept());
    }

    [Fact]
    public async Task InvalidationWhileWaitingForNetworkRevokesTheQueuedTab()
    {
        var oldTranslation = Completion<string>();
        var started = Completion<bool>();
        var canceled = Completion<bool>();
        int writes = 0;
        using var coordinator = new CandidateTranslationCoordinator((observation, token) =>
        {
            if (observation.Version != 1) return Task.FromResult("fresh preview");
            token.Register(() => canceled.TrySetResult(true));
            started.TrySetResult(true);
            return oldTranslation.Task;
        }, (_, _) => Task.FromResult(true), (_, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(true);
        });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.TryRequestAccept());
        coordinator.Invalidate();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldTranslation.SetResult("must not write");
        Assert.False(coordinator.TryRequestAccept());
        coordinator.Observe(Snapshot(2));
        await WaitForStateAsync(coordinator, x => x.Translation == "fresh preview");
        Assert.False(coordinator.State.AcceptRequested);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task InvalidationDuringFinalValidationPreventsTheAcceptorCall()
    {
        var finalValidation = Completion<bool>();
        var validating = Completion<bool>();
        var canceled = Completion<bool>();
        int validations = 0, writes = 0;
        using var coordinator = new CandidateTranslationCoordinator((_, _) => Task.FromResult("English"), (_, token) =>
        {
            if (Interlocked.Increment(ref validations) < 3) return Task.FromResult(true);
            token.Register(() => canceled.TrySetResult(true));
            validating.TrySetResult(true);
            return finalValidation.Task;
        }, (_, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(true);
        });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await WaitForStateAsync(coordinator, x => x.Translation == "English");
        Assert.True(coordinator.TryRequestAccept());
        await validating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Invalidate();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        finalValidation.SetResult(true);
        Assert.Null(coordinator.State.Observation);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task AcceptorReceivesCancellationAtItsActualWriteBoundary()
    {
        var enteredAcceptor = Completion<bool>();
        var releaseBoundary = Completion<bool>();
        var canceled = Completion<bool>();
        var leftBoundary = Completion<bool>();
        int writes = 0;
        using var coordinator = new CandidateTranslationCoordinator((_, _) => Task.FromResult("English"),
            (_, _) => Task.FromResult(true), async (_, _, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            enteredAcceptor.TrySetResult(true);
            try
            {
                await releaseBoundary.Task;
                token.ThrowIfCancellationRequested(); // The real acceptor must also re-check native evidence here.
                Interlocked.Increment(ref writes);
                return true;
            }
            finally { leftBoundary.TrySetResult(true); }
        });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await WaitForStateAsync(coordinator, x => x.Translation == "English");
        Assert.True(coordinator.TryRequestAccept());
        await enteredAcceptor.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Invalidate();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBoundary.SetResult(true);
        await leftBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task FailedAcceptanceRevalidationClearsPreviewWithoutWriting()
    {
        int validations = 0, writes = 0;
        using var coordinator = new CandidateTranslationCoordinator((_, _) => Task.FromResult("English"),
            (_, _) => Task.FromResult(Interlocked.Increment(ref validations) < 3), (_, _, _) =>
            {
                Interlocked.Increment(ref writes);
                return Task.FromResult(true);
            });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await WaitForStateAsync(coordinator, x => x.Translation == "English");
        Assert.True(coordinator.TryRequestAccept());
        await WaitForStateAsync(coordinator, x => x.Observation is null && x.Generation > 2);
        Assert.Equal(3, Volatile.Read(ref validations));
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task BackgroundFailureIsObservableAndTheNextCandidateStillWorks()
    {
        var fault = Completion<Exception>();
        using var coordinator = new CandidateTranslationCoordinator((observation, _) => observation.Version == 1
            ? throw new InvalidOperationException("translation failure") : Task.FromResult("recovered"),
            (_, _) => Task.FromResult(true));
        coordinator.Faulted += _ => throw new InvalidOperationException("fault observer failure");
        coordinator.Faulted += error => fault.TrySetResult(error);
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        Assert.Equal("translation failure", (await fault.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
        Assert.Equal("translation failure", coordinator.State.Error);
        coordinator.Observe(Snapshot(2));
        var recovered = await WaitForStateAsync(coordinator, x => x.Translation == "recovered");
        Assert.Null(recovered.Error);
    }

    [Fact]
    public async Task SubscriberFailuresAreObservedAndOtherSubscribersContinue()
    {
        var fault = Completion<Exception>();
        var delivered = Completion<bool>();
        using var coordinator = new CandidateTranslationCoordinator((_, _) => Task.FromResult("English"),
            (_, _) => Task.FromResult(true));
        coordinator.Changed += _ => throw new InvalidOperationException("subscriber failure");
        coordinator.Changed += state => { if (state.Translation == "English") delivered.TrySetResult(true); };
        coordinator.Faulted += error => fault.TrySetResult(error);
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("subscriber failure", (await fault.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
    }

    [Fact]
    public async Task HooksAndStateMutationsNeverInvokeDelegatesOrEventsInline()
    {
        var inlineCalls = new ConcurrentQueue<bool>();
        var notificationCanReadState = Completion<bool>();
        var accepted = Completion<bool>();
        using var coordinator = new CandidateTranslationCoordinator((_, _) =>
        {
            inlineCalls.Enqueue(_insideHook);
            return Task.FromResult("English");
        }, (_, _) =>
        {
            inlineCalls.Enqueue(_insideHook);
            return Task.FromResult(true);
        }, (_, _, _) =>
        {
            inlineCalls.Enqueue(_insideHook);
            accepted.TrySetResult(true);
            return Task.FromResult(true);
        });
        coordinator.Changed += state =>
        {
            inlineCalls.Enqueue(_insideHook);
            if (state.Translation is not null)
            {
                // If Changed ran under the coordinator lock, this other thread could not read State.
                bool readCompleted = Task.Run(() => coordinator.State).Wait(TimeSpan.FromSeconds(2));
                notificationCanReadState.TrySetResult(readCompleted);
            }
        };
        try
        {
            _insideHook = true;
            coordinator.SetEnabled(true);
            coordinator.Observe(Snapshot(1));
        }
        finally { _insideHook = false; }
        await WaitForStateAsync(coordinator, x => x.Translation == "English");
        Assert.True(await notificationCanReadState.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        try { _insideHook = true; Assert.True(coordinator.TryRequestAccept()); }
        finally { _insideHook = false; }
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(true, inlineCalls);
    }

    [Fact]
    public async Task SourceInvalidationRejectsOlderSnapshotsButAllowsItsOwnVersion()
    {
        using var coordinator = new CandidateTranslationCoordinator((observation, _) => Task.FromResult($"v{observation.Version}"),
            (_, _) => Task.FromResult(true));
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(10));
        await WaitForStateAsync(coordinator, x => x.Translation == "v10");
        coordinator.Invalidate(12);
        Assert.False(coordinator.Observe(Snapshot(11)));
        Assert.True(coordinator.Observe(Snapshot(12)));
        await WaitForStateAsync(coordinator, x => x.Translation == "v12");
        long generation = coordinator.State.Generation;
        coordinator.Invalidate(11); // A delayed observer callback must not invalidate newer evidence.
        Assert.Equal(generation, coordinator.State.Generation);
        Assert.Equal("v12", coordinator.State.Translation);
        Assert.False(coordinator.Observe(Snapshot(13) with { Bounds = default }));
        Assert.Null(coordinator.State.Observation);
    }

    [Fact]
    public async Task DisposeCancelsWaitingAcceptanceAndRejectsFurtherWork()
    {
        var translation = Completion<string>();
        var started = Completion<bool>();
        var canceled = Completion<bool>();
        int writes = 0;
        var coordinator = new CandidateTranslationCoordinator((_, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            started.TrySetResult(true);
            return translation.Task;
        }, (_, _) => Task.FromResult(true), (_, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(true);
        });
        coordinator.SetEnabled(true);
        coordinator.Observe(Snapshot(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.TryRequestAccept());
        coordinator.Dispose();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        translation.SetResult("late");
        Assert.False(coordinator.State.Enabled);
        Assert.Null(coordinator.State.Observation);
        Assert.False(coordinator.TryRequestAccept());
        Assert.False(coordinator.Observe(Snapshot(2)));
        Assert.Equal(0, Volatile.Read(ref writes));
        coordinator.Dispose();
    }
}
