namespace PinyinTranslate.Input;

/// <summary>
/// Immutable evidence for a single IME observation. RuntimeId is a copied value,
/// never a UI Automation COM object. An observer must invalidate the session on
/// every input or focus change, including changes which later restore these values.
/// </summary>
public sealed record InputSnapshot(
    nint ForegroundWindow,
    int ProcessId,
    string FocusRuntimeId,
    string CompositionId,
    string CandidatePageId,
    string Candidate,
    string CaretAndSelectionId);

public readonly record struct InputTicket(long Version, InputSnapshot Snapshot);

/// <summary>
/// Tracks versions independently from text equality so A→B→A cannot resurrect an
/// earlier translation. This class does not observe input, intercept keys, or
/// write to other processes. A future integration must do those checks itself.
/// </summary>
public sealed class InputSession
{
    private readonly object _sync = new();
    private long _version;
    private InputSnapshot? _snapshot;
    private bool _enabled;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _version++;
            _enabled = enabled;
            _snapshot = null;
        }
    }

    /// <summary>Call for every observed mutation, even when the values are equal.</summary>
    public InputTicket? Observe(InputSnapshot? snapshot)
    {
        lock (_sync)
        {
            _version++;
            _snapshot = _enabled && HasCompleteEvidence(snapshot) ? snapshot : null;
            return _snapshot is null ? null : new InputTicket(_version, _snapshot);
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _version++;
            _snapshot = null;
        }
    }

    public bool IsCurrent(InputTicket ticket, InputSnapshot liveSnapshot)
    {
        lock (_sync)
        {
            return _enabled && _snapshot is not null && HasCompleteEvidence(liveSnapshot) && ticket.Version == _version
                && ticket.Snapshot == _snapshot && ticket.Snapshot == liveSnapshot;
        }
    }

    /// <summary>
    /// Missing evidence must fail closed. Nonempty identifiers alone are not
    /// proof: producers must obtain real session, page and caret identities.
    /// </summary>
    private static bool HasCompleteEvidence(InputSnapshot? snapshot) =>
        snapshot is not null && snapshot.ForegroundWindow != 0 && snapshot.ProcessId > 0 &&
        !string.IsNullOrWhiteSpace(snapshot.FocusRuntimeId) &&
        !string.IsNullOrWhiteSpace(snapshot.CompositionId) &&
        !string.IsNullOrWhiteSpace(snapshot.CandidatePageId) &&
        !string.IsNullOrWhiteSpace(snapshot.Candidate) && snapshot.Candidate.Length <= 256 &&
        !string.IsNullOrWhiteSpace(snapshot.CaretAndSelectionId);
}

/// <summary>
/// The current build has no validated IME composition-termination and insertion
/// mechanism. Never intercept Tab or inject text based only on InputSession.
/// </summary>
public static class ImeInputCapability
{
    // These flags describe the old InputSession ticket alone. Production runtime
    // uses per-control EditableContextAdapter evidence and CandidateObserverClient.
    public const bool CanAutomaticallyTranslateCandidate = false;
    public const bool CanSafelyInterceptAndInsert = false;
    public const string UnavailableReason = "旧版 InputSession 票据本身不能授权写入；运行时另行复核控件能力、候选、正文与光标。";
}
