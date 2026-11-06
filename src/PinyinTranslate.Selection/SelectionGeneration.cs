namespace PinyinTranslate.Selection;

/// <summary>Thread-safe generation barrier; each transition invalidates all earlier work, including ABA changes.</summary>
public sealed class SelectionGeneration
{
    private readonly object gate = new();
    private long version;
    private long enabledEpoch;
    private bool enabled;
    private bool disposed;
    private bool awaitingFreshSelection;
    private long acceptedVersion = -1;
    public long SetEnabled(bool value, bool requireFreshSelection = false)
    {
        lock (gate) { if (disposed) return version; enabled = value; ++enabledEpoch; awaitingFreshSelection = value && requireFreshSelection; ++version; acceptedVersion = value && !requireFreshSelection ? version : -1; return version; }
    }
    public bool TryCaptureEpoch(out long captured)
    {
        lock (gate) { captured = enabledEpoch; return enabled && !disposed; }
    }
    public bool TryInvalidateFromEpoch(long captured, out long next)
    {
        lock (gate)
        {
            if (!enabled || disposed || enabledEpoch != captured) { next = version; return false; }
            acceptedVersion = -1; next = ++version; return true;
        }
    }
    public bool TryAcceptSelection(long candidate)
    {
        lock (gate)
        {
            if (!enabled || disposed || version != candidate) return false;
            awaitingFreshSelection = false; acceptedVersion = candidate; return true;
        }
    }
    public bool CanRead(long candidate)
    {
        lock (gate) return enabled && !disposed && version == candidate && !awaitingFreshSelection && acceptedVersion == candidate;
    }
    public long Invalidate()
    {
        lock (gate) { acceptedVersion = -1; return ++version; }
    }
    public long DisposeGeneration()
    {
        lock (gate) { if (!disposed) { disposed = true; enabled = false; ++enabledEpoch; ++version; } return version; }
    }
    public long Current { get { lock (gate) return version; } }
    public bool IsEnabled { get { lock (gate) return enabled && !disposed; } }
    public bool IsCurrent(long candidate) { lock (gate) return enabled && !disposed && version == candidate; }
}
