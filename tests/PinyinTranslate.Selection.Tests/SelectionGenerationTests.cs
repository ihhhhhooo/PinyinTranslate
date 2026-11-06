using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

public sealed class SelectionGenerationTests
{
    [Fact]
    public void DisabledByDefaultAndDisableReenableDoesNotResurrectOldVersion()
    {
        var state = new SelectionGeneration();
        Assert.False(state.IsCurrent(state.Current));
        var first = state.SetEnabled(true);
        Assert.True(state.IsCurrent(first));
        state.SetEnabled(false);
        var next = state.SetEnabled(true);
        Assert.False(state.IsCurrent(first)); Assert.True(state.IsCurrent(next));
    }
    [Fact]
    public void ChangesInvalidateOldVersionEvenWhenTextReturnsToPreviousValue()
    {
        var state = new SelectionGeneration(); var first = state.SetEnabled(true);
        state.Invalidate(); var third = state.Invalidate();
        Assert.False(state.IsCurrent(first)); Assert.True(state.IsCurrent(third));
    }
    [Fact]
    public async Task ConcurrentInvalidationsAreUniqueAndOnlyLastCanBeCurrent()
    {
        var state = new SelectionGeneration(); state.SetEnabled(true);
        long[] versions = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(state.Invalidate)));
        Assert.Equal(100, versions.Distinct().Count());
        Assert.Single(versions, state.IsCurrent); Assert.True(state.IsCurrent(versions.Max()));
    }
    [Fact]
    public async Task LateResponseIsRejectedAfterNewSelectionAndDisable()
    {
        var state = new SelectionGeneration(); var old = state.SetEnabled(true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Task.Run(async () => { await release.Task; return state.IsCurrent(old); });
        var fresh = state.Invalidate(); Assert.True(state.IsCurrent(fresh));
        state.SetEnabled(false); release.SetResult();
        Assert.False(await late); Assert.False(state.IsCurrent(fresh));
    }
    [Fact]
    public void FreshBarrierRejectsOldCallbackAfterCaptureResume()
    {
        var state = new SelectionGeneration();
        var beforeCapture = state.SetEnabled(true);
        Assert.True(state.TryCaptureEpoch(out var oldCallback));
        state.SetEnabled(false);
        var resumed = state.SetEnabled(true, requireFreshSelection: true);
        Assert.False(state.TryInvalidateFromEpoch(oldCallback, out _));
        Assert.False(state.TryAcceptSelection(beforeCapture));
        Assert.False(state.CanRead(resumed));
        Assert.True(state.TryCaptureEpoch(out var captured));
        Assert.True(state.TryInvalidateFromEpoch(captured, out var next));
        Assert.False(state.CanRead(next));
        Assert.True(state.TryAcceptSelection(next));
        Assert.True(state.CanRead(next));
    }
    [Fact]
    public void UnacceptedEventNeverMakesAnOldSelectionReadable()
    {
        var state = new SelectionGeneration();
        var initial = state.SetEnabled(true);
        Assert.True(state.CanRead(initial));
        Assert.True(state.TryCaptureEpoch(out var epoch));
        Assert.True(state.TryInvalidateFromEpoch(epoch, out var unrelated));
        Assert.False(state.CanRead(unrelated));
        Assert.False(state.TryAcceptSelection(initial));
        Assert.False(state.CanRead(unrelated));
    }
    [Fact]
    public async Task ConcurrentCallbacksSharingEpochAllInvalidateAndLatestWins()
    {
        var state = new SelectionGeneration(); state.SetEnabled(true);
        Assert.True(state.TryCaptureEpoch(out long epoch));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            Assert.True(state.TryInvalidateFromEpoch(epoch, out long version));
            return version;
        })).ToArray();
        start.SetResult();
        long[] versions = await Task.WhenAll(tasks);
        Assert.Equal(100, versions.Distinct().Count());
        var latest = versions.Max();
        foreach (long earlier in versions.Where(v => v != latest)) Assert.False(state.TryAcceptSelection(earlier));
        Assert.False(state.CanRead(latest));
        Assert.True(state.TryAcceptSelection(latest));
        Assert.True(state.CanRead(latest));
    }
    [Fact]
    public void OldEpochCannotInvalidateOrUnlockFreshCaptureAfterPause()
    {
        var state = new SelectionGeneration(); state.SetEnabled(true);
        Assert.True(state.TryCaptureEpoch(out long oldEpoch));
        Assert.True(state.TryInvalidateFromEpoch(oldEpoch, out long beforePause));
        state.SetEnabled(false);
        long resumed = state.SetEnabled(true, requireFreshSelection: true);
        Assert.False(state.TryInvalidateFromEpoch(oldEpoch, out _));
        Assert.False(state.TryAcceptSelection(beforePause));
        Assert.False(state.CanRead(resumed));
        Assert.True(state.TryCaptureEpoch(out long nextEpoch));
        Assert.True(state.TryInvalidateFromEpoch(nextEpoch, out long next));
        Assert.True(state.TryAcceptSelection(next));
        Assert.True(state.CanRead(next));
    }
    [Fact]
    public void DisposeRejectsChangesAndOutstandingTickets()
    {
        var state = new SelectionGeneration(); var ticket = state.SetEnabled(true);
        var disposed = state.DisposeGeneration();
        Assert.False(state.IsCurrent(ticket)); Assert.False(state.IsCurrent(disposed));
        Assert.Equal(disposed, state.SetEnabled(true)); Assert.False(state.IsEnabled);
    }
}
