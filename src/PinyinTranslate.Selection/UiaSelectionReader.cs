using System.Diagnostics;

namespace PinyinTranslate.Selection;

/// <summary>The same bounded discovery/range algorithm serves real UIA and synthetic graphs.</summary>
public sealed class UiaSelectionReader : ISelectionReader
{
    private readonly ISelectionEnvironment environment;
    private readonly SelectionReaderOptions options;
    private readonly Dictionary<string, Evidence> previous = new(StringComparer.Ordinal);
    private ISelectionElement? currentSource;
    private long rangeSequence, epoch;
    private bool enabled, disposed;
    private sealed record Evidence(ISelectionElement Source, nint Root, string RuntimeId, string Text,
        IReadOnlyList<ISelectionTextRange>? Ranges, NativeSelectionReadResult? Native, string RangeId);
    private enum Outcome { Unsupported, Empty, Selected, Rejected }
    public event Action<SelectionReaderSignal>? Changed;

    public UiaSelectionReader() : this(new UiaSelectionEnvironment()) { }
    public UiaSelectionReader(ISelectionEnvironment environment, SelectionReaderOptions? options = null)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));
        this.options = options ?? new();
        if (this.options.MaximumTextLength <= 0 || this.options.MaximumRanges <= 0 || this.options.MaximumNodes <= 0 ||
            this.options.MaximumAncestors <= 0 || this.options.DiscoveryBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        environment.Changed += OnChanged;
    }
    public bool Start() => !disposed && environment.Start();
    public void SetEnabled(bool value, long newEpoch)
    {
        enabled = value; epoch = newEpoch; currentSource = null;
        environment.SetEnabled(value, newEpoch);
    }
    private void OnChanged(SelectionReaderSignal signal)
    {
        if (disposed || !enabled || signal.Epoch != epoch) return;
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action<SelectionReaderSignal> handler in handlers.GetInvocationList()) try { handler(signal); } catch { }
    }

    public SelectionReadResult? Read(SelectionReadRequest request)
    {
        if (disposed || !enabled || request.Epoch != epoch) return null;
        var clock = Stopwatch.StartNew();
        nint root = Safe(() => environment.GetWindowRoot(environment.ForegroundWindow));
        if (root == 0) return null;
        if (request.ActivityWindow != 0 && request.Origin is SelectionReadOrigin.Pointer or SelectionReadOrigin.Keyboard &&
            Safe(() => environment.GetWindowRoot(request.ActivityWindow)) != root) return null;
        bool Allowed() => !disposed && enabled && request.Epoch == epoch && clock.Elapsed < options.DiscoveryBudget &&
            Safe(() => environment.GetWindowRoot(environment.ForegroundWindow)) == root;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var eventSource = Safe(environment.TakeEventSource);
        if (eventSource is not null && !IsAllowed(eventSource, root)) eventSource = null;
        bool pointerHintAvailable = request.PointerX.HasValue && request.PointerY.HasValue &&
            (request.ActivityWindow == 0 || Safe(() => environment.GetWindowRoot(request.ActivityWindow)) == root);
        bool pointerScopedRead = pointerHintAvailable && (request.Origin == SelectionReadOrigin.Pointer ||
            (request.Origin == SelectionReadOrigin.Poll && currentSource is null));
        ISelectionElement? PointerTarget() => pointerHintAvailable
            ? Safe(() => environment.ElementFromPoint(request.PointerX!.Value, request.PointerY!.Value)) : null;
        ISelectionElement? primary = request.Origin switch
        {
            SelectionReadOrigin.Pointer when request.PointerX.HasValue && request.PointerY.HasValue =>
                PointerTarget(),
            SelectionReadOrigin.Keyboard or SelectionReadOrigin.Focus => Safe(() => environment.FocusedElement),
            SelectionReadOrigin.SelectionEvent => eventSource ?? currentSource,
            // Some providers update their range after mouse-up without raising
            // an event. Keep the scoped pointer hint until a later input replaces
            // it, so recovery cannot be stolen by an old focused URL selection.
            SelectionReadOrigin.Poll => currentSource ?? PointerTarget(),
            _ => null
        };
        if (primary is not null)
        {
            var preferred = ReadChain(primary, root, Allowed, seen, out bool authoritative);
            if (preferred is not null) return Publish(preferred, eventSource, request);
            // An explicitly targeted caret/cleared selection must not resurrect a
            // stale selection elsewhere in the same browser window.
            if (authoritative) { currentSource = null; return null; }
            // Terminal/embedded-editor hit testing may return an unsupported
            // container, with its TextPattern exposed only on a child. Search
            // that container, never an unrelated focused control. Pointer
            // evidence additionally has to contain the actual gesture point.
            var nested = ReadDescendants(primary, root, request, pointerHintAvailable, Allowed, seen, out bool uncertain);
            if (nested is not null) return Publish(nested, eventSource, request);
            if (uncertain) { currentSource = null; return null; }
        }
        // A pointer-selected area that exposes no readable range is unavailable,
        // rather than permission to translate an unrelated focused selection.
        if (pointerScopedRead) { currentSource = null; return null; }

        // On enable/unknown provider, all discovered selections must agree on one
        // source. This avoids arbitrarily choosing an old URL bar over a document.
        var found = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        void AddChain(ISelectionElement? source)
        {
            if (source is null || !Allowed()) return;
            var evidence = ReadChain(source, root, Allowed, seen, out _);
            if (evidence is not null) found.TryAdd(evidence.RuntimeId, evidence);
        }
        AddChain(eventSource);
        AddChain(Safe(() => environment.FocusedElement));
        if (request.PointerX.HasValue && request.PointerY.HasValue)
            AddChain(Safe(() => environment.ElementFromPoint(request.PointerX.Value, request.PointerY.Value)));
        var foreground = Safe(() => environment.ForegroundElement);
        if (foreground is not null)
        {
            var stack = new Stack<(ISelectionElement Node, int Depth)>();
            stack.Push((foreground, 0));
            int visited = 0;
            while (stack.Count > 0 && visited++ < options.MaximumNodes && Allowed())
            {
                var (node, depth) = stack.Pop();
                try
                {
                    string key = node.RuntimeId;
                    if (seen.Add(key) && TryRead(node, root, Allowed, out var evidence) == Outcome.Selected)
                        found.TryAdd(evidence!.RuntimeId, evidence);
                    if (found.Count > 1) return null;
                    if (depth >= options.MaximumDepth || node.IsPassword || node.IsOffscreen || node.ProcessId == environment.OwnProcessId) continue;
                    var child = node.FirstChild;
                    var children = new List<ISelectionElement>();
                    for (int i = 0; child is not null && i < options.MaximumChildren && Allowed(); i++)
                    { children.Add(child); child = Safe(() => child.NextSibling); }
                    for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
                }
                catch { /* One unavailable provider must not hide later candidates. */ }
            }
        }
        if (!Allowed() || found.Count != 1) { currentSource = null; return null; }
        return Publish(found.Values.Single(), eventSource, request);
    }

    private Evidence? ReadChain(ISelectionElement source, nint root, Func<bool> allowed,
        HashSet<string> seen, out bool authoritative)
    {
        authoritative = false;
        if (!IsAllowed(source, root)) { authoritative = true; return null; }
        ISelectionElement? node = source;
        for (int i = 0; node is not null && i < options.MaximumAncestors && allowed(); i++)
        {
            try
            {
                var candidate = node;
                if (seen.Add(candidate.RuntimeId))
                {
                    var outcome = TryRead(candidate, root, allowed, out var evidence);
                    if (outcome == Outcome.Selected) return evidence;
                    if (outcome == Outcome.Rejected) { authoritative = true; return null; }
                    if (outcome == Outcome.Empty) authoritative = true;
                }
                if (candidate.NativeWindowHandle == root) break;
                node = candidate.Parent;
            }
            catch { node = Safe(() => node!.Parent); }
        }
        return null;
    }

    private Evidence? ReadDescendants(ISelectionElement source, nint root, SelectionReadRequest request,
        bool pointerScoped, Func<bool> allowed, HashSet<string> seen, out bool uncertain)
    {
        uncertain = false;
        if (!IsAllowed(source, root)) return null;
        var pending = new Stack<(ISelectionElement Node, int Depth)>();
        pending.Push((source, 0));
        Evidence? found = null;
        var walked = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0 && walked.Count < options.MaximumNodes && allowed())
        {
            var (node, depth) = pending.Pop();
            try
            {
                if (!walked.Add(node.RuntimeId) || !IsAllowed(node, root)) continue;
                var bounds = pointerScoped ? Safe(() => node.Bounds) : null;
                if (bounds is { IsValid: false }) bounds = null;
                if (pointerScoped && bounds.HasValue && !bounds.Value.Contains(request.PointerX!.Value, request.PointerY!.Value)) continue;
                bool pointVerified = !pointerScoped || (bounds?.Contains(request.PointerX!.Value, request.PointerY!.Value) == true);
                if (pointVerified && seen.Add(node.RuntimeId))
                {
                    var outcome = TryRead(node, root, allowed, out var evidence);
                    if (outcome == Outcome.Selected)
                    {
                        if (found is not null && found.RuntimeId != evidence!.RuntimeId) { uncertain = true; return null; }
                        found = evidence;
                        continue; // This provider owns the selected range; nested proxies are not extra selections.
                    }
                    // Do not use an old selected descendant beneath an explicit
                    // caret or a protected/unreadable selection provider.
                    if (outcome is Outcome.Empty or Outcome.Rejected) continue;
                }
                var child = node.FirstChild;
                if (depth >= options.MaximumDepth)
                {
                    if (child is not null) { uncertain = true; return null; }
                    continue;
                }
                for (int count = 0; child is not null && count < options.MaximumChildren && allowed(); count++)
                { pending.Push((child, depth + 1)); child = child.NextSibling; }
                if (child is not null) { uncertain = true; return null; }
            }
            catch { /* A failing subtree is not evidence of a selected range. */ }
        }
        uncertain = !allowed() || pending.Count != 0;
        return uncertain ? null : found;
    }

    private Outcome TryRead(ISelectionElement node, nint root, Func<bool> allowed, out Evidence? evidence)
    {
        evidence = null;
        if (!allowed() || !IsAllowed(node, root)) return Outcome.Rejected;
        var ranges = node.GetSelectionRanges();
        if (ranges is null)
        {
            var native = environment.ReadNative(node, () => allowed() && IsAllowed(node, root));
            if (native is null) return Outcome.Unsupported;
            if (string.IsNullOrWhiteSpace(native.Text)) return Outcome.Empty;
            if (native.Text.Length > options.MaximumTextLength) return Outcome.Rejected;
            evidence = MakeEvidence(node, root, native.Text, null, native);
            return Outcome.Selected;
        }
        if (ranges.Count > options.MaximumRanges) return Outcome.Rejected;
        var ordered = new List<ISelectionTextRange>();
        foreach (var range in ranges)
        {
            if (!allowed()) return Outcome.Rejected;
            if (range.CompareEndpoint(SelectionRangeEndpoint.Start, range, SelectionRangeEndpoint.End) != 0)
                ordered.Add(range.Clone());
        }
        ordered.Sort((left, right) => left.CompareEndpoint(SelectionRangeEndpoint.Start, right, SelectionRangeEndpoint.Start));
        for (int i = ordered.Count - 1; i > 0; i--)
        {
            var left = ordered[i - 1]; var right = ordered[i];
            if (left.CompareEndpoint(SelectionRangeEndpoint.Start, right, SelectionRangeEndpoint.Start) == 0 &&
                left.CompareEndpoint(SelectionRangeEndpoint.End, right, SelectionRangeEndpoint.End) == 0)
                ordered.RemoveAt(i);
            else if (left.CompareEndpoint(SelectionRangeEndpoint.End, right, SelectionRangeEndpoint.Start) > 0)
                return Outcome.Rejected;
        }
        var parts = new List<string>();
        int length = 0;
        foreach (var range in ordered)
        {
            if (!allowed()) return Outcome.Rejected;
            string text = range.GetText(options.MaximumTextLength + 1);
            if (text.Length == 0) continue;
            length += text.Length + (parts.Count > 0 ? 1 : 0);
            if (length > options.MaximumTextLength) return Outcome.Rejected;
            parts.Add(text);
        }
        if (parts.Count == 0 || parts.All(string.IsNullOrWhiteSpace)) return Outcome.Empty;
        if (!allowed() || !IsAllowed(node, root)) return Outcome.Rejected;
        evidence = MakeEvidence(node, root, string.Join("\n", parts), ordered, null);
        return Outcome.Selected;
    }

    private Evidence MakeEvidence(ISelectionElement node, nint root, string text,
        IReadOnlyList<ISelectionTextRange>? ranges, NativeSelectionReadResult? native)
    {
        string runtime = node.RuntimeId;
        string key = root.ToInt64().ToString("X") + ":" + runtime;
        string id;
        if (previous.TryGetValue(key, out var old) && old.Text == text && EqualRanges(old.Ranges, ranges) && SameNative(old.Native, native)) id = old.RangeId;
        else id = Interlocked.Increment(ref rangeSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = new Evidence(node, root, runtime, text, ranges, native, id);
        if (previous.Count >= 64 && !previous.ContainsKey(key)) previous.Remove(previous.Keys.First());
        previous[key] = result;
        return result;
    }

    private SelectionReadResult Publish(Evidence evidence, ISelectionElement? eventSource, SelectionReadRequest request)
    {
        currentSource = evidence.Source;
        bool fromEvent = request.Origin == SelectionReadOrigin.SelectionEvent && eventSource is not null &&
            IsAncestor(evidence.Source, eventSource) && IsAllowed(eventSource, evidence.Root);
        return new(evidence.Source.ProcessId, unchecked((int)evidence.Root.ToInt64()), evidence.RuntimeId, evidence.Text,
            evidence.RangeId, evidence.Root.ToInt64().ToString("X"), evidence, fromEvent);
    }

    public bool Validate(SelectionReadResult selection)
    {
        if (disposed || !enabled || selection.ValidationToken is not Evidence old) return false;
        nint root = Safe(() => environment.GetWindowRoot(environment.ForegroundWindow));
        if (root != old.Root || !IsAllowed(old.Source, root)) return false;
        var clock = Stopwatch.StartNew();
        bool Allowed() => !disposed && enabled && clock.Elapsed < options.DiscoveryBudget &&
            Safe(() => environment.GetWindowRoot(environment.ForegroundWindow)) == root;
        try
        {
            if (TryRead(old.Source, root, Allowed, out var now) != Outcome.Selected || now is null) return false;
            return Allowed() && now.RuntimeId == old.RuntimeId && now.Text == old.Text &&
                EqualRanges(old.Ranges, now.Ranges) && SameNative(old.Native, now.Native);
        }
        catch { return false; }
    }

    private bool IsAllowed(ISelectionElement source, nint root)
    {
        ISelectionElement? node = source;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (int i = 0; node is not null && i < options.MaximumAncestors; i++)
            {
                if (node.ProcessId <= 0 || node.ProcessId == environment.OwnProcessId || node.IsPassword || node.IsOffscreen || !seen.Add(node.RuntimeId)) return false;
                nint hwnd = node.NativeWindowHandle;
                if (hwnd != 0 && environment.GetWindowRoot(hwnd) != root) return false;
                if (hwnd == root) return true;
                node = node.Parent;
            }
        }
        catch { }
        return false;
    }

    private bool IsAncestor(ISelectionElement ancestor, ISelectionElement child)
    {
        try
        {
            ISelectionElement? node = child;
            for (int i = 0; node is not null && i < options.MaximumAncestors; i++, node = node.Parent)
                if (node.RuntimeId == ancestor.RuntimeId) return true;
        }
        catch { }
        return false;
    }
    private static bool EqualRanges(IReadOnlyList<ISelectionTextRange>? left, IReadOnlyList<ISelectionTextRange>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        try
        {
            for (int i = 0; i < left.Count; i++)
                if (left[i].CompareEndpoint(SelectionRangeEndpoint.Start, right[i], SelectionRangeEndpoint.Start) != 0 ||
                    left[i].CompareEndpoint(SelectionRangeEndpoint.End, right[i], SelectionRangeEndpoint.End) != 0) return false;
            return true;
        }
        catch { return false; }
    }
    private static bool SameNative(NativeSelectionReadResult? left, NativeSelectionReadResult? right) => left == right;
    private static T? Safe<T>(Func<T?> action) { try { return action(); } catch { return default; } }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; enabled = false; environment.Changed -= OnChanged;
        previous.Clear(); currentSource = null; environment.Dispose();
    }
}
