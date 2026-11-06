using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Injected HWND lists and counters only. No process enumeration, UIA or native calls.
public sealed class CandidateHostDiscoveryCacheTests
{
    [Fact]
    public void ContinuousTypingReusesHostsAndUnrelatedDesktopHintsDoNotRescanUia()
    {
        long now = 0;
        int frameReads = 0, hostScans = 0;
        var cache = new CandidateHostDiscoveryCache(() => now);
        IReadOnlyList<nint> Frames() { frameReads++; return [11, 22]; }
        void Scan(IReadOnlyList<nint> _) => hostScans++;
        Assert.True(cache.RefreshIfNeeded(1, false, false, false, Frames, Scan));
        for (int i = 0; i < 100; i++)
        {
            now += 100;
            Assert.False(cache.RefreshIfNeeded(1, true, false, false, Frames, Scan));
        }
        Assert.Equal(1, frameReads);
        for (int i = 0; i < 20; i++)
            Assert.False(cache.RefreshIfNeeded(1, true, true, false, Frames, Scan));
        Assert.Equal(21, frameReads);
        Assert.Equal(1, hostScans);
    }

    [Fact]
    public void NewFrameAndNewModeEpochRediscoverImmediatelyWithoutTrustingOldSubscriptions()
    {
        long now = 0;
        int scans = 0;
        IReadOnlyList<nint> frames = [11];
        var cache = new CandidateHostDiscoveryCache(() => now);
        bool Refresh(long epoch) => cache.RefreshIfNeeded(epoch, true, true, false, () => frames, _ => scans++);
        Assert.True(Refresh(1));
        frames = [22, 11, 22];
        Assert.True(Refresh(1));
        frames = [11, 22]; // Enumeration order and duplicates are not topology changes.
        Assert.False(Refresh(1));
        Assert.True(Refresh(2));
        Assert.Equal(3, scans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrStaleHostsAreRetriedWithoutScanningForEveryHint(bool hasHosts)
    {
        long now = 0;
        int scans = 0;
        var cache = new CandidateHostDiscoveryCache(() => now);
        bool Refresh() => cache.RefreshIfNeeded(1, hasHosts, true, hasHosts, () => [11], _ => scans++);
        Assert.True(Refresh());
        for (now = 1; now < 500; now++) Assert.False(Refresh());
        Assert.True(Refresh());
        Assert.Equal(2, scans);
    }

    [Fact]
    public void AFailedDiscoveryDoesNotMarkTheEpochOrFramesAsSuccessfullyCached()
    {
        var cache = new CandidateHostDiscoveryCache(() => 0);
        Assert.Throws<InvalidOperationException>(() => cache.RefreshIfNeeded(1, false, false, false, () => [11],
            _ => throw new InvalidOperationException("Synthetic unavailable provider")));
        int scans = 0;
        Assert.True(cache.RefreshIfNeeded(1, false, false, false, () => [11], _ => scans++));
        Assert.Equal(1, scans);
    }

    [Fact]
    public void AnInitiallyEmptyFrameListCanRecoverWhenTheInputHostAppears()
    {
        long now = 0;
        var cache = new CandidateHostDiscoveryCache(() => now);
        Assert.True(cache.RefreshIfNeeded(1, false, false, false, () => [], _ => { }));
        now = 20;
        IReadOnlyList<nint>? discovered = null;
        Assert.True(cache.RefreshIfNeeded(1, false, true, false, () => [42], frames => discovered = frames));
        Assert.Equal((nint)42, Assert.Single(discovered!));
    }
}
