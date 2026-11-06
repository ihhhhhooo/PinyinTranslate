namespace PinyinTranslate.Selection;

public enum SelectionRangeEndpoint { Start, End }

public readonly record struct SelectionBounds(double X, double Y, double Width, double Height)
{
    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
    public bool Contains(int x, int y) => IsValid && x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>The reader uses only true TextPattern ranges, never Name or Value text.</summary>
public interface ISelectionTextRange
{
    string GetText(int maximumLength);
    ISelectionTextRange Clone();
    int CompareEndpoint(SelectionRangeEndpoint endpoint, ISelectionTextRange other, SelectionRangeEndpoint otherEndpoint);
}

public interface ISelectionElement
{
    int ProcessId { get; }
    string RuntimeId { get; }
    nint NativeWindowHandle { get; }
    bool IsPassword { get; }
    bool IsOffscreen => false;
    SelectionBounds? Bounds => null;
    ISelectionElement? Parent { get; }
    ISelectionElement? FirstChild { get; }
    ISelectionElement? NextSibling { get; }
    /// <returns>null only when neither TextPattern nor TextPattern2 is available.</returns>
    IReadOnlyList<ISelectionTextRange>? GetSelectionRanges();
}

/// <summary>Injectable UIA graph. Only the production implementation creates COM in Start.</summary>
public interface ISelectionEnvironment : IDisposable
{
    int OwnProcessId { get; }
    event Action<SelectionReaderSignal>? Changed;
    bool Start();
    void SetEnabled(bool enabled, long epoch);
    nint ForegroundWindow { get; }
    nint GetWindowRoot(nint window);
    ISelectionElement? ForegroundElement { get; }
    ISelectionElement? FocusedElement { get; }
    ISelectionElement? ElementFromPoint(int x, int y);
    ISelectionElement? TakeEventSource();
    NativeSelectionReadResult? ReadNative(ISelectionElement element, Func<bool> stillAllowed);
}

public sealed record SelectionReaderOptions
{
    public int MaximumTextLength { get; init; } = 4000;
    public int MaximumRanges { get; init; } = 32;
    public int MaximumAncestors { get; init; } = 32;
    public int MaximumNodes { get; init; } = 128;
    public int MaximumDepth { get; init; } = 8;
    public int MaximumChildren { get; init; } = 32;
    public TimeSpan DiscoveryBudget { get; init; } = TimeSpan.FromMilliseconds(750);
}
