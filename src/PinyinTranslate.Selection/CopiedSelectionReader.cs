namespace PinyinTranslate.Selection;

public sealed record CopySelectionRequest(nint Window, int ProcessId, uint BeforeSequence);
public sealed record CopiedSelection(nint Window, int ProcessId, uint Sequence, string Text)
{
    /// <summary>Explicit repeat-copy of a stable clipboard still owned by its source app.</summary>
    public bool SourceOwnedBaseline { get; init; }
}
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
/// it does not require a readable selection or a preceding selection gesture.
/// Start on key-down; repeat keys and the chord's releases are not new activity.
/// The pre-key sequence is captured by the native hook,
/// before the target receives the copy command; a later async callback is too late.
/// </summary>
public sealed class CopySelectionIntentTracker(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private CopySelectionRequest? chord;
    private SelectionActivityModifiers chordModifiers;
    private bool cReleased;
    public bool HasActiveChord => chord is not null;

    public void Reset() { chord = null; chordModifiers = 0; cReleased = false; }
    public static bool IsCopyShortcut(SelectionActivity value) => value.Kind == SelectionActivityKind.KeyDown && value.VirtualKey == 0x43 &&
        (value.Modifiers & SelectionActivityModifiers.Control) != 0 &&
        (value.Modifiers & (SelectionActivityModifiers.Alt | SelectionActivityModifiers.Windows)) == 0 && !value.IsInjected;
    private static SelectionActivityModifiers Modifier(uint key) => key switch
    {
        0x10 or 0xA0 or 0xA1 => SelectionActivityModifiers.Shift,
        0x11 or 0xA2 or 0xA3 => SelectionActivityModifiers.Control,
        _ => SelectionActivityModifiers.None
    };

    /// <summary>Called at the activity barrier before advancing its revision.</summary>
    public bool IsContinuation(SelectionActivity value)
    {
        if (chord is null) return false;
        if (value.IsInjected || value.TargetWindow != chord.Window || value.TargetProcessId != chord.ProcessId)
        { Reset(); return false; }
        if (IsCopyShortcut(value) && value.IsRepeat) return true;
        var modifier = Modifier(value.VirtualKey);
        if (value.Kind == SelectionActivityKind.KeyUp && (value.VirtualKey == 0x43 || (modifier & chordModifiers) != 0))
        {
            if (value.VirtualKey == 0x43) cReleased = true;
            if (cReleased && value.Modifiers == SelectionActivityModifiers.None) Reset();
            return true;
        }
        Reset(); return false;
    }

    public CopySelectionRequest? Observe(SelectionActivity value)
    {
        long time = now();
        if (value.IsInjected || value.TargetWindow == 0 || value.TargetProcessId <= 0)
        { Reset(); return null; }
        if (IsContinuation(value)) return null;
        long occurred = value.OccurredAtMilliseconds > 0 ? value.OccurredAtMilliseconds : time;
        if (IsCopyShortcut(value) && !value.IsRepeat && value.HasCopySequence && time - occurred is >= 0 and <= 15000)
        {
            chord = new(value.TargetWindow, value.TargetProcessId, value.BeforeCopySequence);
            chordModifiers = value.Modifiers; cReleased = false;
            return chord;
        }
        return null;
    }
}
