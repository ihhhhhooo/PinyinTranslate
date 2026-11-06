namespace PinyinTranslate.Selection;

public sealed record JavaSelectionWindow(nint Window, int ProcessId);
public interface IJavaSelectionBackend : IDisposable
{
    JavaSelectionWindow? GetForegroundJavaWindow();
    string? UnavailableReason { get; }
    JavaSelectedText? Read(JavaSelectionWindow window, int? x, int? y, Func<bool> allowed);
}

/// <summary>Polling/gesture-driven Java selection adapter; no synthesized Copy or input.</summary>
public sealed class JavaSelectionReader(IJavaSelectionBackend backend) : ISelectionReader
{
    private volatile bool enabled, disposed;
    private long epoch;
    private sealed record Evidence(JavaSelectionWindow Window, JavaSelectedText Selection, int? X, int? Y, long Epoch);
    public event Action<SelectionReaderSignal>? Changed { add { } remove { } }
    public string? UnavailableReason { get; private set; }
    public bool Start() => !disposed;
    public void SetEnabled(bool value, long newEpoch) { Interlocked.Exchange(ref epoch, newEpoch); enabled = value; UnavailableReason = null; }
    public SelectionReadResult? Read(SelectionReadRequest request)
    {
        UnavailableReason = null;
        if (!enabled || disposed || request.Epoch != Interlocked.Read(ref epoch)) return null;
        var window = backend.GetForegroundJavaWindow();
        if (window is null ||
            (request.ActivityWindow != 0 && request.ActivityWindow != window.Window &&
             request.Origin is SelectionReadOrigin.Pointer or SelectionReadOrigin.Keyboard)) return null;
        bool Allowed() => !disposed && enabled && Interlocked.Read(ref epoch) == request.Epoch && backend.GetForegroundJavaWindow() == window;
        var value = backend.Read(window, request.PointerX, request.PointerY, Allowed);
        if (!Allowed()) return null;
        UnavailableReason = backend.UnavailableReason;
        if (value is null || value.SourceVersion <= 0 || value.Start < 0 || value.End <= value.Start ||
            (long)value.End - value.Start != value.Text.Length || value.Text.Length > JavaSelectionCapture.MaximumTextLength ||
            string.IsNullOrWhiteSpace(value.Text)) return null;
        var evidence = new Evidence(window, value, request.PointerX, request.PointerY, request.Epoch);
        return new(window.ProcessId, unchecked((int)window.Window.ToInt64()), $"java:{window.ProcessId}:{value.SourceVersion}",
            value.Text, $"{value.SourceVersion}:{value.Start}:{value.End}", window.Window.ToInt64().ToString("X"), evidence);
    }
    public bool Validate(SelectionReadResult selection)
    {
        if (!enabled || disposed || selection.ValidationToken is not Evidence evidence) return false;
        long captured = Interlocked.Read(ref epoch);
        if (captured != evidence.Epoch) return false;
        bool Allowed() => !disposed && enabled && Interlocked.Read(ref epoch) == captured && backend.GetForegroundJavaWindow() == evidence.Window;
        return Allowed() && backend.Read(evidence.Window, evidence.X, evidence.Y, Allowed) == evidence.Selection && Allowed();
    }
    public void Dispose() { disposed = true; enabled = false; backend.Dispose(); }
}

/// <summary>The existing UIA/native route remains the fallback for non-Java and UIA-enabled Java providers.</summary>
public sealed class DesktopSelectionReader : ISelectionReader
{
    private readonly ISelectionReader uia, java;
    private sealed record Evidence(ISelectionReader Reader, SelectionReadResult Selection);
    public DesktopSelectionReader() : this(new UiaSelectionReader(), new JavaSelectionReader(new WindowsJavaSelectionBackend())) { }
    public DesktopSelectionReader(ISelectionReader uia, ISelectionReader java)
    {
        this.uia = uia; this.java = java;
        uia.Changed += OnChanged; java.Changed += OnChanged;
    }
    public event Action<SelectionReaderSignal>? Changed;
    public string? UnavailableReason => java.UnavailableReason ?? uia.UnavailableReason;
    private void OnChanged(SelectionReaderSignal signal) => Changed?.Invoke(signal);
    public bool Start()
    {
        bool windows = false, jvm = false;
        try { windows = uia.Start(); } catch { }
        try { jvm = java.Start(); } catch { }
        return windows || jvm;
    }
    public void SetEnabled(bool enabled, long epoch) { uia.SetEnabled(enabled, epoch); java.SetEnabled(enabled, epoch); }
    public SelectionReadResult? Read(SelectionReadRequest request)
    {
        var result = java.Read(request);
        var source = java;
        if (result is null) { result = uia.Read(request); source = uia; }
        return result is null ? null : result with { ValidationToken = new Evidence(source, result) };
    }
    public bool Validate(SelectionReadResult selection) => selection.ValidationToken is Evidence evidence && evidence.Reader.Validate(evidence.Selection);
    public void Dispose() { uia.Changed -= OnChanged; java.Changed -= OnChanged; java.Dispose(); uia.Dispose(); }
}
