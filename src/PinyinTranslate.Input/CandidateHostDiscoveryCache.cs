namespace PinyinTranslate.Input;

/// <summary>
/// Worker-confined discovery schedule. Reuses live host subscriptions during typing;
/// desktop structure hints first compare cheap HWND metadata before a full UIA scan.
/// </summary>
public sealed class CandidateHostDiscoveryCache(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _epoch = long.MinValue, _lastRefresh;
    private HashSet<nint> _frames = [];
    private bool _initialized;

    public bool RefreshIfNeeded(long epoch, bool hasHosts, bool topologyHint, bool readFailed,
        Func<IReadOnlyList<nint>> discoverFrames, Action<IReadOnlyList<nint>> refreshHosts)
    {
        bool freshEpoch = !_initialized || epoch != _epoch;
        if (!freshEpoch && hasHosts && !topologyHint && !readFailed) return false;

        var frames = discoverFrames().Distinct().ToArray();
        bool changed = !_frames.SetEquals(frames);
        if (!freshEpoch && hasHosts && !changed && !readFailed) return false;
        long now = _clock();
        // Empty/stale providers can emit many hints. Retry discovery without
        // returning to an expensive scan for every key or unrelated window event.
        if (!freshEpoch && !changed && now - _lastRefresh is >= 0 and < 500) return false;

        refreshHosts(frames);
        _frames = new(frames);
        _epoch = epoch;
        _lastRefresh = _clock();
        _initialized = true;
        return true;
    }
}
