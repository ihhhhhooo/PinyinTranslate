using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using PinyinTranslate.App;
using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

/// <summary>
/// Managed delegates and uninitialized services only. No product constructor,
/// runtime Dispose, UI dispatcher, UIA, hook, clipboard or real Process is used.
/// </summary>
public sealed class UpdatePreparationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparationRetainsTheExactModeSnapshotUntilExplicitResume(bool ime, bool selection)
    {
        var expected = new UpdateModeState(ime, selection);
        UpdateModeState? restored = null;
        int captures = 0, pauses = 0, resumes = 0;
        var preparation = new UpdatePreparation(() => { captures++; return expected; },
            _ => { pauses++; return Task.CompletedTask; },
            modes => { resumes++; restored = modes; return Task.CompletedTask; });
        await preparation.PrepareAsync(default);
        await preparation.PrepareAsync(default);
        Assert.True(preparation.IsActive);
        Assert.Null(restored);
        Assert.Equal(1, captures);
        Assert.Equal(1, pauses);
        await preparation.ResumeAsync();
        await preparation.ResumeAsync();
        Assert.False(preparation.IsActive);
        Assert.Equal(expected, restored);
        Assert.Equal(1, resumes);
    }

    [Fact]
    public async Task PrepareFailureRestoresModesAndLeavesTheApplicationAvailable()
    {
        var original = new UpdateModeState(true, true);
        UpdateModeState? restored = null;
        var preparation = new UpdatePreparation(() => original,
            _ => Task.FromException(new IOException("fake-stop-failed")),
            modes => { restored = modes; return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(default));
        Assert.Contains("程序仍在运行", error.Message);
        Assert.Equal(original, restored);
        Assert.False(preparation.IsActive);
        await preparation.ResumeAsync(); // The updater's fallback resume is idempotent.
    }

    [Fact]
    public async Task CallerCancellationRestoresThePriorModes()
    {
        int restores = 0;
        using var cancellation = new CancellationTokenSource();
        var preparation = new UpdatePreparation(() => new(true, false),
            token => Task.Delay(Timeout.Infinite, token),
            modes => { Assert.Equal(new(true, false), modes); restores++; return Task.CompletedTask; });
        Task pending = preparation.PrepareAsync(cancellation.Token);
        Assert.True(preparation.IsActive);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(preparation.IsActive);
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task ADrainIgnoringCancellationIsBoundedAndLateCompletionDoesNotPrepareAgain()
    {
        var lateDrain = Completion();
        int restores = 0;
        var preparation = new UpdatePreparation(() => new(true, false), _ => lateDrain.Task,
            _ => { restores++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(default));
        Assert.False(preparation.IsActive);
        Assert.Equal(1, restores);
        lateDrain.SetResult(true);
        await lateDrain.Task;
        Assert.False(preparation.IsActive);
        await preparation.ResumeAsync();
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task ConcurrentPreparationAndResumeUseOnePauseAndOneRestore()
    {
        var drain = Completion();
        int captures = 0, pauses = 0, restores = 0;
        var preparation = new UpdatePreparation(() => { captures++; return new(true, true); },
            _ => { pauses++; return drain.Task; }, _ => { restores++; return Task.CompletedTask; });
        Task first = preparation.PrepareAsync(default);
        Task duplicate = preparation.PrepareAsync(default);
        Assert.False(duplicate.IsCompleted);
        drain.SetResult(true);
        await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(preparation.ResumeAsync(), preparation.ResumeAsync());
        Assert.False(preparation.IsActive);
        Assert.Equal(1, captures);
        Assert.Equal(1, pauses);
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task ResumeWaitsForThePendingPreparationBeforeRestoring()
    {
        var drain = Completion();
        int restores = 0;
        var preparation = new UpdatePreparation(() => new(true, false), _ => drain.Task,
            _ => { restores++; return Task.CompletedTask; });
        Task prepare = preparation.PrepareAsync(default);
        Task resume = preparation.ResumeAsync();
        Assert.False(resume.IsCompleted);
        Assert.Equal(0, restores);
        drain.SetResult(true);
        await Task.WhenAll(prepare, resume).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(preparation.IsActive);
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task AlreadyCancelledPreparationDoesNotCaptureOrPause()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var preparation = new UpdatePreparation(() => throw new Exception("must-not-capture"),
            _ => throw new Exception("must-not-pause"), _ => throw new Exception("must-not-resume"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.PrepareAsync(cancellation.Token));
        Assert.False(preparation.IsActive);
    }

    [Fact]
    public async Task FailedModeRestoreStillClearsTheBarrierSoManualControlsRemainUsable()
    {
        int snapshots = 0;
        var preparation = new UpdatePreparation(() => { snapshots++; return new(true, true); },
            _ => Task.CompletedTask, _ => Task.FromException(new IOException("fake-restore-failed")));
        await preparation.PrepareAsync(default);
        await Assert.ThrowsAsync<IOException>(() => preparation.ResumeAsync());
        Assert.False(preparation.IsActive);
        await preparation.PrepareAsync(default);
        Assert.Equal(2, snapshots);
    }

    [Fact]
    public async Task ActualServiceEntrypointsRejectNewActivityBeforeTouchingAbsentNativeFields()
    {
        var service = (DesktopServices)RuntimeHelpers.GetUninitializedObject(typeof(DesktopServices));
        var drain = Completion();
        var preparation = new UpdatePreparation(() => new(true, true), _ => drain.Task, _ => Task.CompletedTask);
        Set(service, "_updatePreparation", preparation);
        Task preparing = service.PrepareForUpdateAsync(default);
        Assert.True(service.IsPreparingForUpdate);
        Assert.NotNull(await service.SetImeEnabledAsync(true));
        Assert.NotNull(await service.SetSelectionEnabledAsync(true));
        Assert.NotNull(await service.ApplySettingsAsync(new("fake-ime", "fake-selection", "fake-capture")));
        Assert.NotNull(await service.SetThemeAsync("light"));
        Assert.NotNull(await service.SaveApiKeyAsync("fake-test-canary"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateScreenshotAsync([], default));
        Assert.Throws<InvalidOperationException>(() => service.SuspendSelectionForCapture());
        typeof(DesktopServices).GetMethod("HotkeyFired", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [1]);
        // All native service fields remain null. A missed barrier would throw.
        drain.SetResult(true);
        await preparing;
        await service.ResumeAfterUpdateFailureAsync();
        Assert.False(service.IsPreparingForUpdate);
    }

    [Fact]
    public async Task ActualRuntimeStopClosesAdmissionBeforeDisablingAndWaitsForEveryOwnedChild()
    {
        var runtime = (ImeRuntimeController)RuntimeHelpers.GetUninitializedObject(typeof(ImeRuntimeController));
        var tracker = new UpdateQuiescenceTracker();
        var child = new FakeObserver();
        Set(runtime, "_gate", new object());
        Set(runtime, "_updateQuiescence", tracker);
        Set(runtime, "_enabled", 1);
        Set(runtime, "_disableForUpdateAsync", new Func<Task>(() =>
        {
            Assert.True(tracker.IsPaused);
            Assert.False(runtime.Enabled);
            Set(runtime, "_enabled", 0);
            return Task.CompletedTask;
        }));
        tracker.Register(() => child.StopAsync());
        Task stopping = runtime.StopForUpdateAsync(default);
        await child.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(tracker.TryBeginOperation());
        Assert.False(stopping.IsCompleted);
        Assert.NotNull(await runtime.SetEnabledAsync(true)); // Rejected before any null native fields.
        child.Exit.TrySetResult(true);
        await stopping;
        runtime.ResumeAfterUpdateFailure();
        Assert.False(tracker.IsPaused);
        using var newFactory = tracker.TryBeginOperation();
        Assert.NotNull(newFactory);
        Assert.Equal(1, child.Stops);
    }

    [Fact]
    public async Task ActualRuntimeCancelledStopRemainsBlockedUntilExplicitResume()
    {
        var runtime = (ImeRuntimeController)RuntimeHelpers.GetUninitializedObject(typeof(ImeRuntimeController));
        var tracker = new UpdateQuiescenceTracker();
        using var inFlight = Assert.IsAssignableFrom<IDisposable>(tracker.TryBeginOperation());
        Set(runtime, "_gate", new object());
        Set(runtime, "_updateQuiescence", tracker);
        Set(runtime, "_disableForUpdateAsync", new Func<Task>(() => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        Task stopping = runtime.StopForUpdateAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        Assert.True(tracker.IsPaused);
        Assert.NotNull(await runtime.SetEnabledAsync(true));
        runtime.ResumeAfterUpdateFailure();
        Assert.False(tracker.IsPaused);
    }

    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Set(object instance, string field, object? value) => instance.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private sealed class FakeObserver : ICandidateObserverShutdown
    {
        public readonly TaskCompletionSource<bool> Exit = Completion();
        public readonly TaskCompletionSource<bool> StopEntered = Completion();
        public int Stops;
        Task ICandidateObserverShutdown.Completion => Exit.Task;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stops); StopEntered.TrySetResult(true);
            return Exit.Task.WaitAsync(cancellationToken);
        }
    }
}
