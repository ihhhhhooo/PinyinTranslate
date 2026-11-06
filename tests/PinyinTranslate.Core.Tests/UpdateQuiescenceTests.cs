using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

/// <summary>The real lifetime/drain algorithm with in-memory tasks and fake child processes only.</summary>
public sealed class UpdateQuiescenceTests
{
    [Fact]
    public async Task PausingWaitsForAnAlreadyDetachedResourceAndStopsItOnlyOnce()
    {
        var tracker = new UpdateQuiescenceTracker();
        var exit = Completion();
        int stops = 0;
        var detached = tracker.Register(() => { Interlocked.Increment(ref stops); return exit.Task; });
        Task detachedStop = detached.StopAsync();
        Task drain = tracker.PauseAndDrainAsync();
        Assert.True(tracker.IsPaused);
        Assert.Null(tracker.TryBeginOperation());
        Assert.False(drain.IsCompleted);
        exit.SetResult(true);
        await Task.WhenAll(detachedStop, drain).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task InFlightFactoryMustAttachAndStopItsChildBeforeDrainCanComplete()
    {
        var tracker = new UpdateQuiescenceTracker();
        using var factory = Assert.IsAssignableFrom<IDisposable>(tracker.TryBeginOperation());
        Task drain = tracker.PauseAndDrainAsync();
        Assert.False(drain.IsCompleted);
        var stopEntered = Completion();
        var exit = Completion();
        tracker.Register(() => { stopEntered.TrySetResult(true); return exit.Task; });
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Dispose();
        Assert.False(drain.IsCompleted);
        exit.SetResult(true);
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(tracker.TryBeginOperation());
        tracker.Resume();
        using var afterFailure = tracker.TryBeginOperation();
        Assert.NotNull(afterFailure);
    }

    [Fact]
    public async Task StopFailureRetainsOwnershipAndCanBeRetried()
    {
        var tracker = new UpdateQuiescenceTracker();
        int attempts = 0;
        tracker.Register(() => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new IOException("fake-stop-failed")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => tracker.PauseAndDrainAsync());
        Assert.True(tracker.IsPaused);
        await tracker.PauseAndDrainAsync();
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancellingTheWaitDoesNotForgetTheOwnedResourceOrReleaseAdmission()
    {
        var tracker = new UpdateQuiescenceTracker();
        var exit = Completion();
        tracker.Register(() => exit.Task);
        using var cancellation = new CancellationTokenSource();
        Task drain = tracker.PauseAndDrainAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
        Assert.True(tracker.IsPaused);
        Assert.Null(tracker.TryBeginOperation());
        tracker.Resume();
        using (var newFactory = tracker.TryBeginOperation()) Assert.NotNull(newFactory);
        Task retry = tracker.PauseAndDrainAsync();
        Assert.False(retry.IsCompleted);
        exit.SetResult(true);
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConcurrentStopWaitsConfirmActualExitBeforeDisposing()
    {
        var process = new FakeProcess();
        var shutdown = new OwnedChildProcessShutdown(process);
        Task first = shutdown.StopAsync();
        await process.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task second = shutdown.StopAsync();
        Assert.Equal(1, process.Kills);
        Assert.Equal(0, process.Disposals);
        Assert.False(first.IsCompleted);
        process.Exit();
        await Task.WhenAll(first, second);
        Assert.Equal(1, process.Kills);
        Assert.Equal(1, process.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedIdentityRefusesToKillOrDisposeAnUnconfirmedProcess(bool changePid)
    {
        var process = new FakeProcess();
        var shutdown = new OwnedChildProcessShutdown(process);
        if (changePid) process.ProcessId++;
        else process.StartTimeUtcTicks++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown.StopAsync());
        Assert.Equal(0, process.Kills);
        Assert.Equal(0, process.Waits);
        Assert.Equal(0, process.Disposals);
    }

    [Fact]
    public async Task AlreadyExitedChildIsConfirmedWithoutKill()
    {
        var process = new FakeProcess();
        process.Exit();
        var shutdown = new OwnedChildProcessShutdown(process);
        await shutdown.StopAsync();
        Assert.Equal(0, process.Kills);
        Assert.Equal(1, process.Waits);
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task ACompletedWaitWithoutActualExitIsNotSuccess()
    {
        var process = new FakeProcess { ReturnPrematurely = true };
        var shutdown = new OwnedChildProcessShutdown(process);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown.StopAsync());
        Assert.Equal(0, process.Disposals);
    }

    [Fact]
    public async Task ExitBetweenQueryAndKillIsAcceptedOnlyAfterExitConfirmation()
    {
        var process = new FakeProcess { ExitDuringKill = true };
        var shutdown = new OwnedChildProcessShutdown(process);
        await shutdown.StopAsync();
        Assert.Equal(1, process.Kills);
        Assert.Equal(1, process.Waits);
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task TimedOutChildRemainsOwnedAndCanBeConfirmedOnRetry()
    {
        var process = new FakeProcess();
        var shutdown = new OwnedChildProcessShutdown(process, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => shutdown.StopAsync());
        Assert.Equal(1, process.Kills);
        Assert.Equal(0, process.Disposals);
        process.Exit();
        await shutdown.StopAsync();
        Assert.Equal(1, process.Kills);
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task CancelledCallerDoesNotTerminateAnyChild()
    {
        var process = new FakeProcess();
        var shutdown = new OwnedChildProcessShutdown(process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown.StopAsync(cancellation.Token));
        Assert.Equal(0, process.Kills);
        Assert.Equal(0, process.Waits);
    }

    [Fact]
    public async Task KillFailureDoesNotDisposeAndASecondAttemptCanSucceed()
    {
        var process = new FakeProcess { DenyKill = true };
        var shutdown = new OwnedChildProcessShutdown(process);
        await Assert.ThrowsAsync<IOException>(() => shutdown.StopAsync());
        Assert.Equal(0, process.Disposals);
        process.DenyKill = false;
        process.ExitDuringKill = true;
        await shutdown.StopAsync();
        Assert.Equal(2, process.Kills);
        Assert.Equal(1, process.Disposals);
    }

    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class FakeProcess : IOwnedChildProcess
    {
        private readonly TaskCompletionSource<bool> exit = Completion();
        public readonly TaskCompletionSource<bool> WaitEntered = Completion();
        public int ProcessId { get; set; } = 42;
        public long StartTimeUtcTicks { get; set; } = 1234;
        public bool HasExited { get; private set; }
        public bool ReturnPrematurely, ExitDuringKill, DenyKill;
        public int Kills, Waits, Disposals;
        public void Kill()
        {
            Kills++;
            if (DenyKill) throw new IOException("fake-kill-denied");
            if (ExitDuringKill) { Exit(); throw new InvalidOperationException("fake-already-exited"); }
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            Waits++;
            WaitEntered.TrySetResult(true);
            // Deliberately ignores cancellation, exercising the outer wait bound.
            return ReturnPrematurely ? Task.CompletedTask : exit.Task;
        }
        public void Exit() { HasExited = true; exit.TrySetResult(true); }
        public void Dispose() => Disposals++;
    }
}
