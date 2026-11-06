using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// The production discovery/validation algorithm traverses a managed graph.
// No COM object, UIA provider, HWND, native reader, hook or clipboard is created.
public sealed class UiaSelectionReaderTests
{
    [Fact]
    public void ReadOnlyDocumentAncestorCanSupplySelectionWhenTheFocusedLeafHasNone()
    {
        var graph = new Graph();
        var document = graph.Root.Add(Node.Selected("document", "Selected body text"));
        var leaf = document.Add(new Node("focused-leaf") { Ranges = [] });
        graph.Focus = leaf;
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(result); Assert.Equal("Selected body text", result.Text);
        Assert.Equal("document", result.RuntimeId);
        graph.Focus = graph.Root.Add(new Node("unrelated-same-window-focus"));
        Assert.True(reader.Validate(result)); // Focus need not stay in an editable field.
    }

    [Fact]
    public void PointerDocumentBeatsAnOldSelectionInTheUrlBarAndPollKeepsThatSource()
    {
        var graph = new Graph();
        var url = graph.Root.Add(Node.Selected("url", "https://old-address.example"));
        var document = graph.Root.Add(Node.Selected("document", "New article selection"));
        graph.Focus = url;
        graph.Point = document.Add(new Node("point-leaf") { Ranges = [] });
        using var reader = Reader(graph);
        var pointed = reader.Read(new(1, SelectionReadOrigin.Pointer, 20, 30, 100));
        Assert.NotNull(pointed); Assert.Equal("New article selection", pointed.Text);
        var polled = reader.Read(new(1, SelectionReadOrigin.Poll));
        Assert.NotNull(polled); Assert.Equal(pointed.RangeId, polled.RangeId); Assert.Equal(pointed.RuntimeId, polled.RuntimeId);
    }

    [Fact]
    public void PointerCaretWithoutAnAncestorSelectionCannotFallBackToTheOldUrlBar()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("url", "old address"));
        graph.Point = graph.Root.Add(new Node("empty-document") { Ranges = [new Range(3, 3, "")] });
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Pointer, 20, 30, 100)));
    }

    [Fact]
    public void EventSourceDocumentBeatsOldFocusedUrlSelection()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("url", "old address"));
        var document = graph.Root.Add(Node.Selected("document", "Event-selected body text"));
        var eventLeaf = document.Add(new Node("event-leaf") { Ranges = [] });
        using var reader = Reader(graph);
        graph.EventSource = eventLeaf;
        var result = reader.Read(new(1, SelectionReadOrigin.SelectionEvent));
        Assert.NotNull(result); Assert.Equal("Event-selected body text", result.Text);
        Assert.True(result.FromForegroundEvent);
    }

    [Fact]
    public void BackgroundEventCannotClearOrReplaceTheCurrentForegroundSelection()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("url", "old address"));
        graph.Point = graph.Root.Add(Node.Selected("document", "Foreground body text"));
        using var reader = Reader(graph);
        var first = reader.Read(new(1, SelectionReadOrigin.Pointer, 1, 2, 100));
        Assert.NotNull(first);
        var background = new Node("background-root") { NativeWindowHandle = 200 };
        graph.EventSource = background.Add(Node.Selected("background-selection", "Never use this"));
        var after = reader.Read(new(1, SelectionReadOrigin.SelectionEvent));
        Assert.NotNull(after); Assert.Equal(first.Text, after.Text); Assert.Equal(first.RangeId, after.RangeId);
        Assert.False(after.FromForegroundEvent);
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("password", true)]
    [InlineData("hidden", false)]
    [InlineData("hidden", true)]
    [InlineData("own", false)]
    [InlineData("own", true)]
    public void ProtectedTargetCannotClimbToAnOldSelectionInItsVisibleParent(string denied, bool pointer)
    {
        var graph = new Graph();
        var parent = graph.Root.Add(Node.Selected("parent-document", "Old parent selection"));
        var child = parent.Add(new Node("target") { IsPassword = denied == "password", IsOffscreen = denied == "hidden" });
        if (denied == "own") child.ProcessId = graph.OwnProcessId;
        graph.Focus = child; graph.Point = child;
        using var reader = Reader(graph);
        var request = pointer ? new SelectionReadRequest(1, SelectionReadOrigin.Pointer, 1, 2, 100) :
            new SelectionReadRequest(1, SelectionReadOrigin.Keyboard, ActivityWindow: 100);
        Assert.Null(reader.Read(request));
        Assert.Equal(0, child.RangeReads); Assert.Equal(0, parent.RangeReads);
    }

    [Fact]
    public void RendererProcessMayDifferFromTheForegroundHostWhenAncestryBelongsToItsWindow()
    {
        var graph = new Graph();
        var body = Node.Selected("renderer-document", "Web renderer selection");
        body.ProcessId = 43;
        graph.Root.Add(body);
        graph.Focus = body;
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.SelectionEvent));
        Assert.NotNull(result); Assert.Equal(43, result.ProcessId); Assert.Equal("Web renderer selection", result.Text);
    }

    [Fact]
    public void BrokenCandidateDoesNotPreventAValidForegroundDocumentFromBeingRead()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(new Node("broken") { ThrowOnRanges = true });
        graph.Root.Add(Node.Selected("document", "A readable paragraph"));
        using var reader = Reader(graph);
        Assert.Equal("A readable paragraph", reader.Read(new(1, SelectionReadOrigin.Enable))!.Text);
    }

    [Fact]
    public void MultipleRangesAreSortedAndJoinedAndProviderReorderingDoesNotChangeIdentity()
    {
        var graph = new Graph();
        var first = new Range(0, 5, "first");
        var second = new Range(10, 16, "second");
        var third = new Range(20, 25, "third");
        var document = graph.Root.Add(new Node("document") { Ranges = [third, first, second] });
        graph.Focus = document;
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(result); Assert.Equal("first\nsecond\nthird", result.Text);
        Assert.True(reader.Validate(result));
        document.Ranges = [first, second, third];
        var polled = reader.Read(new(1, SelectionReadOrigin.Poll));
        Assert.NotNull(polled); Assert.Equal(result.RangeId, polled.RangeId);
    }

    [Fact]
    public void DuplicateRangesAreNotDuplicatedInTheTranslationText()
    {
        var graph = new Graph();
        var range = new Range(0, 4, "word");
        graph.Focus = graph.Root.Add(new Node("document") { Ranges = [range, range.Clone()] });
        using var reader = Reader(graph);
        Assert.Equal("word", reader.Read(new(1, SelectionReadOrigin.Keyboard))!.Text);
    }

    [Fact]
    public void OverlappingRangesAreRejectedInsteadOfInventingAJoinedSelection()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(new Node("document") { Ranges = [new Range(0, 4, "word"), new Range(2, 6, "ords")] });
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Keyboard)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040204), true)]
    [InlineData(unchecked((int)0x80004002), true)]
    [InlineData(unchecked((int)0x80004001), true)]
    [InlineData(unchecked((int)0x80040200), false)]
    [InlineData(unchecked((int)0x80040201), false)]
    [InlineData(unchecked((int)0x80131505), false)]
    [InlineData(unchecked((int)0x80070005), false)]
    public void OnlyUnsupportedPatternErrorsMayPermitNativeFallback(int error, bool expected) =>
        Assert.Equal(expected, SelectionPatternErrors.IsUnsupported(error));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidationChecksEveryRangeEndpointEvenWhenTheJoinedTextIsUnchanged(int changed)
    {
        var graph = new Graph();
        Range[] ranges = [new(0, 5, "first"), new(10, 16, "second"), new(20, 25, "third")];
        graph.Focus = graph.Root.Add(new Node("document") { Ranges = ranges.Cast<ISelectionTextRange>().ToList() });
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(result);
        ranges[changed].Start++; ranges[changed].End++;
        Assert.False(reader.Validate(result));
    }

    [Fact]
    public void SameWordAtAnotherLocationGetsAnotherRangeId()
    {
        var graph = new Graph();
        var range = new Range(0, 4, "word");
        graph.Focus = graph.Root.Add(new Node("document") { Ranges = [range] });
        using var reader = Reader(graph);
        var first = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(first);
        var stable = reader.Read(new(1, SelectionReadOrigin.Poll));
        Assert.Equal(first.RangeId, stable!.RangeId);
        range.Start = 10; range.End = 14;
        var moved = reader.Read(new(1, SelectionReadOrigin.Poll));
        Assert.NotNull(moved); Assert.Equal(first.Text, moved.Text); Assert.NotEqual(first.RangeId, moved.RangeId);
        Assert.False(reader.Validate(first));
    }

    [Fact]
    public void BackgroundOwnPasswordAndHiddenAncestryAreRejectedBeforeReadingTheirText()
    {
        foreach (string denied in new[] { "background", "own", "password", "hidden" })
        {
            var graph = new Graph();
            var document = Node.Selected("protected-document", "must not read");
            var parent = denied == "background" ? new Node("other-root") { NativeWindowHandle = 200 } : graph.Root;
            if (denied == "password") parent.IsPassword = true;
            if (denied == "hidden") parent.IsOffscreen = true;
            if (denied == "own") document.ProcessId = graph.OwnProcessId;
            parent.Add(document); graph.Focus = document; graph.Point = document;
            using var reader = Reader(graph);
            Assert.Null(reader.Read(new(1, SelectionReadOrigin.Pointer, 1, 2, 100)));
            Assert.Equal(0, document.RangeReads);
        }
    }

    [Fact]
    public void NoPreferredSourceAndMultipleRealSelectionsAreAmbiguous()
    {
        var graph = new Graph();
        graph.Root.Add(Node.Selected("one", "shared selection"));
        graph.Root.Add(Node.Selected("two", "shared selection"));
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Enable)));
    }

    [Fact]
    public void ExistingTextPatternWithNoSelectedRangeDoesNotInvokeNativeFallback()
    {
        var graph = new Graph();
        graph.Root.Ranges = [];
        graph.Focus = graph.Root;
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Keyboard)));
        Assert.Equal(0, graph.NativeReads);
    }

    [Fact]
    public void NativeFallbackEvidenceUsesTheSameScopeGuardAndPositionForValidation()
    {
        var graph = new Graph();
        var document = graph.Root.Add(new Node("native-edit") { NativeWindowHandle = 110 });
        graph.Focus = document;
        var current = new NativeSelectionReadResult(110, 42, "native selection", 5, 21, new string('A', 64));
        graph.Native = (element, allowed) => ReferenceEquals(element, document) && allowed() ? current : null;
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(result); Assert.Equal(current.Text, result.Text); Assert.True(reader.Validate(result));
        current = current with { Start = 25, End = 41 };
        Assert.False(reader.Validate(result));
    }

    [Fact]
    public void OversizedRealRangesAreRejectedInsteadOfTruncated()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("document", new string('x', 4001)));
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Keyboard)));
    }

    [Fact]
    public void A_delayed_pointer_selection_is_recovered_without_an_event_or_choosing_the_old_url_selection()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("old-url", "Old address selection"));
        var document = graph.Root.Add(new Node("document") { Ranges = [] });
        graph.Point = document.Add(new Node("text-leaf") { Ranges = [] });
        using var reader = Reader(graph);
        // The provider has not updated GetSelection when the mouse first releases.
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Pointer, 20, 30, 100)));
        document.Ranges = [new Range(0, 16, "New article text", "article")];
        // No UIA event arrives. Polling must retain the known pointer target.
        var recovered = reader.Read(new(1, SelectionReadOrigin.Poll, 20, 30, 100));
        Assert.NotNull(recovered);
        Assert.Equal("New article text", recovered.Text);
        Assert.Equal("document", recovered.RuntimeId);
    }

    [Fact]
    public void An_unreadable_pointer_target_does_not_authorize_an_unrelated_focused_selection()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("old-url", "Old address selection"));
        graph.Point = graph.Root.Add(new Node("unreadable-document")).Add(new Node("unreadable-text"));
        using var reader = Reader(graph);
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Pointer, 20, 30, 100)));
        Assert.Null(reader.Read(new(1, SelectionReadOrigin.Poll, 20, 30, 100)));
    }

    [Fact]
    public void ForegroundChangeInvalidatesAnOtherwiseIdenticalRealRange()
    {
        var graph = new Graph();
        graph.Focus = graph.Root.Add(Node.Selected("document", "selected paragraph"));
        using var reader = Reader(graph);
        var result = reader.Read(new(1, SelectionReadOrigin.Keyboard));
        Assert.NotNull(result);
        graph.ForegroundWindow = 200;
        Assert.False(reader.Validate(result));
    }

    private static UiaSelectionReader Reader(Graph graph)
    {
        var reader = new UiaSelectionReader(graph);
        Assert.True(reader.Start()); reader.SetEnabled(true, 1);
        return reader;
    }

    private sealed class Node(string runtimeId) : ISelectionElement
    {
        public int ProcessId { get; set; } = 42;
        public string RuntimeId { get; } = runtimeId;
        public nint NativeWindowHandle { get; set; }
        public bool IsPassword { get; set; }
        public bool IsOffscreen { get; set; }
        public ISelectionElement? Parent { get; set; }
        public ISelectionElement? FirstChild { get; private set; }
        public ISelectionElement? NextSibling { get; set; }
        public List<ISelectionTextRange>? Ranges;
        public bool ThrowOnRanges;
        public int RangeReads;
        public Node Add(Node child)
        {
            child.Parent = this;
            if (FirstChild is null) FirstChild = child;
            else { var last = FirstChild; while (last.NextSibling is not null) last = last.NextSibling; ((Node)last).NextSibling = child; }
            return child;
        }
        public IReadOnlyList<ISelectionTextRange>? GetSelectionRanges()
        {
            RangeReads++;
            if (ThrowOnRanges) throw new InvalidOperationException("Synthetic unavailable provider");
            return Ranges;
        }
        public static Node Selected(string id, string text) => new(id) { Ranges = [new Range(0, text.Length, text, id)] };
    }

    private sealed class Range(int start, int end, string text, string document = "main") : ISelectionTextRange
    {
        public int Start = start, End = end;
        public string Text = text;
        public string GetText(int maximumLength) => Text[..Math.Min(Text.Length, maximumLength)];
        public ISelectionTextRange Clone() => new Range(Start, End, Text, document);
        public int CompareEndpoint(SelectionRangeEndpoint endpoint, ISelectionTextRange other, SelectionRangeEndpoint otherEndpoint)
        {
            if (other is not Range value || value.Document != document) throw new InvalidOperationException("Different documents");
            return (endpoint == SelectionRangeEndpoint.Start ? Start : End).CompareTo(otherEndpoint == SelectionRangeEndpoint.Start ? value.Start : value.End);
        }
        private string Document => document;
    }

    private sealed class Graph : ISelectionEnvironment
    {
        public Node Root { get; } = new("root") { NativeWindowHandle = 100 };
        public ISelectionElement? Focus, Point, EventSource;
        public int OwnProcessId => 7;
        public nint ForegroundWindow { get; set; } = 100;
        public ISelectionElement? ForegroundElement => Root;
        public ISelectionElement? FocusedElement => Focus;
        public Func<ISelectionElement, Func<bool>, NativeSelectionReadResult?> Native { get; set; } = (_, _) => null;
        public int NativeReads;
        public bool Start() => true;
        public void SetEnabled(bool enabled, long epoch) { }
        public nint GetWindowRoot(nint window) => window == 100 || window == 110 ? (nint)100 : window;
        public ISelectionElement? ElementFromPoint(int x, int y) => Point;
        public ISelectionElement? TakeEventSource() { var source = EventSource; EventSource = null; return source; }
        public NativeSelectionReadResult? ReadNative(ISelectionElement element, Func<bool> stillAllowed) { NativeReads++; return Native(element, stillAllowed); }
        public event Action<SelectionReaderSignal>? Changed { add { } remove { } }
        public void Dispose() { }
    }
}
