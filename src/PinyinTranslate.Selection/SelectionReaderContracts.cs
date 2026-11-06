namespace PinyinTranslate.Selection;

public interface ISelectionMonitor : IDisposable
{
    Task<bool> Ready { get; }
    bool IsCurrent(long version);
    Task<bool> ValidateAsync(SelectionSnapshot snapshot);
    void SetEnabled(bool enabled, bool requireFreshSelection = false);
    event Action<long>? Invalidated;
    event Action<SelectionSnapshot>? SelectionChanged;
    event Action<string>? StatusChanged;
}

/// <summary>Immutable selection evidence; live ranges are retained only by the reader worker.</summary>
public sealed record SelectionSnapshot(long Version, int ProcessId, int WindowHandle, string RuntimeId, string Text)
{
    public string RangeId { get; init; } = "";
    public string ScopeId { get; init; } = "";
    public long ActivityRevision { get; init; }
    public bool IsCopiedText { get; init; }
}

public enum SelectionReadOrigin { Poll, Enable, Focus, SelectionEvent, Keyboard, Pointer }
public enum SelectionReaderChangeKind { Selection, Focus }
public sealed record SelectionReaderSignal(long Epoch, SelectionReaderChangeKind Kind, bool ForegroundScopeVerified = false);
public sealed record SelectionReadRequest(long Epoch, SelectionReadOrigin Origin = SelectionReadOrigin.Poll,
    int? PointerX = null, int? PointerY = null, nint ActivityWindow = 0);
public sealed record SelectionReadResult(int ProcessId, int WindowHandle, string RuntimeId, string Text,
    string RangeId, string ScopeId, object? ValidationToken = null, bool FromForegroundEvent = false);

/// <summary>All lifecycle/read/validate calls occur on one Monitor worker. Events carry no live UIA objects.</summary>
public interface ISelectionReader : IDisposable
{
    string? UnavailableReason => null;
    event Action<SelectionReaderSignal>? Changed;
    bool Start();
    void SetEnabled(bool enabled, long epoch);
    SelectionReadResult? Read(SelectionReadRequest request);
    bool Validate(SelectionReadResult selection);
}

public sealed record SelectionMonitorOptions
{
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(350);
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumTextLength { get; init; } = 4000;
}
