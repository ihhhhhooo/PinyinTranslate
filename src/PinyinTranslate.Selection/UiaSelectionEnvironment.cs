using System.Runtime.InteropServices;
using FlaUI.UIA3;
using Interop.UIAutomationClient;

namespace PinyinTranslate.Selection;

/// <summary>UIA3-only adapter. COM creation/registration/reads/disposal run on the reader MTA.</summary>
internal sealed class UiaSelectionEnvironment : ISelectionEnvironment
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    private readonly object eventGate = new();
    private readonly NativeSelectionReader native = new();
    private UIA3Automation? automation;
    private IUIAutomationElement? desktop, pendingEventSource;
    private SelectionEventHandler? selectionHandler;
    private FocusEventHandler? focusHandler;
    private long epoch;
    private bool enabled, disposed;
    public int OwnProcessId { get; private set; }
    public event Action<SelectionReaderSignal>? Changed;
    public bool Start()
    {
        if (disposed) return false;
        OwnProcessId = Environment.ProcessId;
        automation = new UIA3Automation
        {
            TransactionTimeout = TimeSpan.FromMilliseconds(250),
            ConnectionTimeout = TimeSpan.FromMilliseconds(250)
        };
        desktop = automation.NativeAutomation.GetRootElement();
        return desktop is not null;
    }
    public nint ForegroundWindow => GetForegroundWindow();
    public nint GetWindowRoot(nint window) => window == 0 ? 0 : GetAncestor(window, 2);
    public ISelectionElement? ForegroundElement => automation is null ? null : Wrap(automation.NativeAutomation.ElementFromHandle(GetForegroundWindow()));
    public ISelectionElement? FocusedElement => automation is null ? null : Wrap(automation.NativeAutomation.GetFocusedElement());
    public ISelectionElement? ElementFromPoint(int x, int y) => automation is null ? null :
        Wrap(automation.NativeAutomation.ElementFromPoint(new tagPOINT { x = x, y = y }));
    public ISelectionElement? TakeEventSource()
    {
        lock (eventGate)
        {
            var source = pendingEventSource;
            pendingEventSource = null;
            return Wrap(source);
        }
    }
    public NativeSelectionReadResult? ReadNative(ISelectionElement element, Func<bool> stillAllowed) =>
        element.NativeWindowHandle == 0 ? null : native.Read(element.NativeWindowHandle, stillAllowed);

    public void SetEnabled(bool value, long newEpoch)
    {
        lock (eventGate) { enabled = false; epoch = newEpoch; pendingEventSource = null; }
        RemoveHandlers();
        if (disposed || !value || automation is null || desktop is null) return;
        var cache = automation.NativeAutomation.CreateCacheRequest();
        cache.AddProperty(UIA_PropertyIds.UIA_ProcessIdPropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_NativeWindowHandlePropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_IsPasswordPropertyId);
        cache.AddProperty(UIA_PropertyIds.UIA_RuntimeIdPropertyId);
        selectionHandler = new SelectionEventHandler(sender => Signal(sender, newEpoch, SelectionReaderChangeKind.Selection));
        focusHandler = new FocusEventHandler(sender => Signal(sender, newEpoch, SelectionReaderChangeKind.Focus));
        lock (eventGate) enabled = true;
        // Providers often omit one event class. Polling remains active even if
        // registration is unavailable; failure here must not kill the worker.
        try { automation.NativeAutomation.AddAutomationEventHandler(UIA_EventIds.UIA_Text_TextSelectionChangedEventId,
            desktop, TreeScope.TreeScope_Subtree, cache, selectionHandler); } catch { }
        try { automation.NativeAutomation.AddFocusChangedEventHandler(cache, focusHandler); } catch { }
    }
    private void Signal(IUIAutomationElement sender, long capturedEpoch, SelectionReaderChangeKind kind)
    {
        try
        {
            // Cached event metadata avoids live provider reads in callbacks.
            if (sender.CachedProcessId == OwnProcessId || sender.CachedIsPassword != 0) return;
            nint window = sender.CachedNativeWindowHandle;
            nint foreground = GetWindowRoot(GetForegroundWindow());
            bool verified = window != 0 && GetWindowRoot(window) == foreground;
            // A known background HWND must not replace the foreground hint.
            if (window != 0 && !verified) return;
            Action<SelectionReaderSignal>? handlers;
            lock (eventGate)
            {
                if (disposed || !enabled || epoch != capturedEpoch) return;
                pendingEventSource = sender;
                handlers = Changed;
            }
            if (handlers is not null)
                foreach (Action<SelectionReaderSignal> handler in handlers.GetInvocationList())
                    try { handler(new(capturedEpoch, kind, verified)); } catch { }
        }
        catch { /* A malformed event cannot revoke an unrelated selection. */ }
    }
    private NativeElement? Wrap(IUIAutomationElement? element) => element is null || automation is null ? null : new(this, element);
    private void RemoveHandlers()
    {
        if (automation is null) return;
        if (selectionHandler is not null && desktop is not null)
            try { automation.NativeAutomation.RemoveAutomationEventHandler(UIA_EventIds.UIA_Text_TextSelectionChangedEventId, desktop, selectionHandler); } catch { }
        if (focusHandler is not null)
            try { automation.NativeAutomation.RemoveFocusChangedEventHandler(focusHandler); } catch { }
        selectionHandler = null; focusHandler = null;
    }
    public void Dispose()
    {
        lock (eventGate) { if (disposed) return; disposed = true; enabled = false; pendingEventSource = null; }
        RemoveHandlers(); desktop = null;
        automation?.Dispose(); automation = null;
    }

    private sealed class NativeElement(UiaSelectionEnvironment owner, IUIAutomationElement element) : ISelectionElement
    {
        private string? runtime;
        public int ProcessId => element.CurrentProcessId;
        public string RuntimeId => runtime ??= string.Join(".", element.GetRuntimeId());
        public nint NativeWindowHandle => element.CurrentNativeWindowHandle;
        public bool IsPassword => element.CurrentIsPassword != 0;
        public bool IsOffscreen => element.CurrentIsOffscreen != 0;
        public ISelectionElement? Parent => owner.Wrap(owner.automation!.NativeAutomation.RawViewWalker.GetParentElement(element));
        public ISelectionElement? FirstChild => owner.Wrap(owner.automation!.NativeAutomation.RawViewWalker.GetFirstChildElement(element));
        public ISelectionElement? NextSibling => owner.Wrap(owner.automation!.NativeAutomation.RawViewWalker.GetNextSiblingElement(element));
        public IReadOnlyList<ISelectionTextRange>? GetSelectionRanges()
        {
            IUIAutomationTextPattern? text = Pattern<IUIAutomationTextPattern>(UIA_PatternIds.UIA_TextPatternId);
            IUIAutomationTextRangeArray? selection;
            if (text is not null) selection = text.GetSelection();
            else
            {
                var text2 = Pattern<IUIAutomationTextPattern2>(UIA_PatternIds.UIA_TextPattern2Id);
                if (text2 is null) return null;
                selection = text2.GetSelection();
            }
            if (selection is null) return Array.Empty<ISelectionTextRange>();
            int count = selection.Length;
            // Return an over-limit sentinel group; never materialize arbitrary
            // provider-sized arrays before the bounded algorithm can reject it.
            if (count > 32) return Enumerable.Repeat<ISelectionTextRange>(new UnreadableRange(), 33).ToArray();
            var ranges = new List<ISelectionTextRange>(count);
            for (int i = 0; i < count; i++) ranges.Add(new NativeRange(selection.GetElement(i)));
            return ranges;
        }
        private T? Pattern<T>(int id) where T : class
        {
            try { return element.GetCurrentPattern(id) as T; }
            catch (COMException error) when (SelectionPatternErrors.IsUnsupported(error.HResult)) { return null; }
        }
    }
    private sealed class NativeRange(IUIAutomationTextRange range) : ISelectionTextRange
    {
        private readonly IUIAutomationTextRange nativeRange = range;
        public string GetText(int maximumLength) => nativeRange.GetText(maximumLength);
        public ISelectionTextRange Clone() => new NativeRange(nativeRange.Clone());
        public int CompareEndpoint(SelectionRangeEndpoint endpoint, ISelectionTextRange other, SelectionRangeEndpoint otherEndpoint) =>
            other is NativeRange native ? nativeRange.CompareEndpoints(Convert(endpoint), native.nativeRange, Convert(otherEndpoint)) : throw new ArgumentException("Range provider mismatch.");
        private static TextPatternRangeEndpoint Convert(SelectionRangeEndpoint endpoint) => endpoint == SelectionRangeEndpoint.Start
            ? TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start : TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;
    }
    private sealed class UnreadableRange : ISelectionTextRange
    {
        public string GetText(int maximumLength) => throw new InvalidOperationException();
        public ISelectionTextRange Clone() => throw new InvalidOperationException();
        public int CompareEndpoint(SelectionRangeEndpoint endpoint, ISelectionTextRange other, SelectionRangeEndpoint otherEndpoint) => throw new InvalidOperationException();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SelectionEventHandler(Action<IUIAutomationElement> action) : IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(IUIAutomationElement sender, int eventId) { try { action(sender); } catch { } }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FocusEventHandler(Action<IUIAutomationElement> action) : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) { try { action(sender); } catch { } }
    }
}

public static class SelectionPatternErrors
{
    public static bool IsUnsupported(int hresult) => hresult is unchecked((int)0x80040204) or
        unchecked((int)0x80004002) or unchecked((int)0x80004001);
}
