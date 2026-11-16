namespace PinyinTranslate.Selection;

public sealed record CopySelectionRequest(nint Window, int ProcessId, uint BeforeSequence);
public sealed record CopiedSelection(nint Window, int ProcessId, uint Sequence, string Text);
public enum CopySelectionStatus { Success, NoNewText, NoText, TooLong, ScopeChanged, Busy, TimedOut, Cancelled, Unavailable }
public sealed record CopySelectionResult(CopySelectionStatus Status, CopiedSelection? Selection = null);

public interface ISelectionClipboardReader : IDisposable
{
    Task<CopySelectionResult> ReadAfterCopyAsync(CopySelectionRequest request, Func<bool> allowed, CancellationToken cancellation);
    /// <summary>The captured text is immutable. Only its request/focus scope remains live.</summary>
    bool IsCurrent(CopiedSelection selection);
}

/// <summary>
/// A physical Ctrl+C/Ctrl+Shift+C is an explicit copy intent in any external app;
/// it does not require a readable selection or a preceding selection gesture. Wait
/// for C and its modifiers to be released so their key-ups do not cancel the
/// resulting translation. The pre-key sequence is captured by the native hook,
/// before the target receives the copy command; a later async callback is too late.
/// </summary>
public sealed class CopySelectionIntentTracker(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long copyAt;
    private CopySelectionRequest? pending;
    private bool cReleased;
    public bool HasPendingCopy
    {
        get
        {
            if (pending is not null && now() - copyAt is < 0 or > 15000) Reset();
            return pending is not null;
        }
    }

    public void Reset() { pending = null; copyAt = 0; cReleased = false; }
    public static bool IsCopyShortcut(SelectionActivity value) => value.Kind == SelectionActivityKind.KeyDown && value.VirtualKey == 0x43 &&
        (value.Modifiers & SelectionActivityModifiers.Control) != 0 &&
        (value.Modifiers & (SelectionActivityModifiers.Alt | SelectionActivityModifiers.Windows)) == 0 && !value.IsInjected;
    private static bool IsModifier(uint key) => key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    public CopySelectionRequest? Observe(SelectionActivity value)
    {
        long time = now();
        if (value.IsInjected || value.TargetWindow == 0 || value.TargetProcessId <= 0)
        { Reset(); return null; }
        if (pending is not null && (pending.Window != value.TargetWindow || pending.ProcessId != value.TargetProcessId ||
            time - copyAt is < 0 or > 15000)) Reset();
        if (pending is not null)
        {
            if (value.Kind == SelectionActivityKind.KeyUp && (value.VirtualKey == 0x43 || IsModifier(value.VirtualKey)))
            {
                if (value.VirtualKey == 0x43) cReleased = true;
                if (cReleased && value.Modifiers == SelectionActivityModifiers.None)
                { var request = pending; pending = null; cReleased = false; return request; }
                return null;
            }
            if (IsCopyShortcut(value) && value.IsRepeat) return null;
            Reset();
        }
        long occurred = value.OccurredAtMilliseconds > 0 ? value.OccurredAtMilliseconds : time;
        if (IsCopyShortcut(value) && !value.IsRepeat && value.HasCopySequence && time - occurred is >= 0 and <= 15000)
        {
            pending = new(value.TargetWindow, value.TargetProcessId, value.BeforeCopySequence); copyAt = occurred; cReleased = false;
        }
        else if (value.Kind is SelectionActivityKind.PointerDown or SelectionActivityKind.PointerWheel ||
            value.Kind == SelectionActivityKind.KeyDown && !IsModifier(value.VirtualKey)) Reset();
        return null;
    }
}
