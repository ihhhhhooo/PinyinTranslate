using PinyinTranslate.Selection;
using System.Runtime.InteropServices;

namespace PinyinTranslate.Selection.Tests;

// Managed Java references/providers only. Native DLLs/message pumps are never started.
public sealed class JavaSelectionReaderTests
{
    [Fact]
    public void Real_selection_is_read_without_the_document_and_java_references_are_released()
    {
        var api = new FakeAccess();
        using (var capture = new JavaSelectionCapture(api))
        {
            var first = capture.Read(100, null, null, () => true);
            Assert.NotNull(first); Assert.Equal("selected text", first.Text); Assert.Equal(7, first.Start); Assert.Equal(20, first.End);
            Assert.Equal(first, capture.Read(100, null, null, () => true));
            Assert.Equal(1, api.Outstanding);
            Assert.All(api.ReadRanges, range => Assert.Equal((7, 20), range));
        }
        Assert.Equal(0, api.Outstanding);
    }

    [Fact]
    public void Selection_longer_than_the_1024_character_native_summary_is_read_in_full()
    {
        var api = new FakeAccess { Document = "prefix " + new string('a', 3000), Range = (7, 3007) };
        using var capture = new JavaSelectionCapture(api);
        Assert.Equal(new string('a', 3000), capture.Read(100, null, null, () => true)!.Text);
        Assert.All(api.ReadRanges, range => Assert.Equal((7, 3007), range));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("password-parent")]
    [InlineData("hidden")]
    [InlineData("not-text")]
    [InlineData("empty")]
    [InlineData("too-long")]
    [InlineData("outside-point")]
    [InlineData("no-root")]
    public void Unreadable_or_protected_sources_never_read_text(string failure)
    {
        var api = new FakeAccess();
        switch (failure)
        {
            case "password": api.Editor = api.Editor with { Role = "password text" }; break;
            case "password-parent": api.Root = api.Root with { Role = "password text" }; break;
            case "hidden": api.Editor = api.Editor with { States = "visible" }; break;
            case "not-text": api.Editor = api.Editor with { HasText = false }; break;
            case "empty": api.Range = (7, 7); break;
            case "too-long": api.Range = (0, 4001); break;
            case "outside-point": api.Editor = api.Editor with { Bounds = new(500, 500, 100, 100) }; break;
            case "no-root": api.RootAvailable = false; break;
        }
        using (var capture = new JavaSelectionCapture(api))
        {
            Assert.Null(capture.Read(100, 20, 30, () => true)); Assert.Empty(api.ReadRanges);
            if (failure == "no-root") Assert.Contains("Java Access Bridge", capture.UnavailableReason);
            if (failure == "too-long") Assert.Contains("4000", capture.UnavailableReason);
        }
        Assert.Equal(0, api.Outstanding);
    }

    [Fact]
    public void Another_editor_with_the_same_text_and_offsets_has_a_new_source_identity()
    {
        var api = new FakeAccess();
        using var capture = new JavaSelectionCapture(api);
        var first = capture.Read(100, null, null, () => true)!;
        api.EditorIdentity = 2;
        var second = capture.Read(100, null, null, () => true)!;
        Assert.Equal(first.Text, second.Text); Assert.NotEqual(first.SourceVersion, second.SourceVersion);
        Assert.Equal(1, api.Outstanding);
    }

    [Fact]
    public void Text_or_selection_changing_during_capture_is_not_published()
    {
        var api = new FakeAccess();
        api.BeforeReadText = () => api.Range = (6, 19);
        using var capture = new JavaSelectionCapture(api);
        Assert.Null(capture.Read(100, null, null, () => true)); Assert.Equal(0, api.Outstanding);
    }

    [Fact]
    public void Invalidation_while_the_provider_is_reading_drops_the_result_and_releases_references()
    {
        bool allowed = true;
        var api = new FakeAccess { BeforeReadText = () => allowed = false };
        using var capture = new JavaSelectionCapture(api);
        Assert.Null(capture.Read(100, null, null, () => allowed)); Assert.Equal(0, api.Outstanding);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(0)]
    public void Broken_UTF16_or_NUL_is_not_sent_to_translation(int codePoint)
    {
        string invalid = new((char)codePoint, 1);
        var api = new FakeAccess { Document = invalid, Range = (0, invalid.Length) };
        using var capture = new JavaSelectionCapture(api);
        Assert.Null(capture.Read(100, null, null, () => true));
    }

    [Fact]
    public void Reader_revalidates_source_selection_foreground_and_mode_epoch()
    {
        var backend = new FakeBackend();
        using var reader = new JavaSelectionReader(backend);
        reader.Start(); reader.SetEnabled(true, 3);
        var result = reader.Read(new(3, SelectionReadOrigin.Pointer, 20, 30, 100));
        Assert.NotNull(result); Assert.True(reader.Validate(result));
        Assert.Equal((20, 30), backend.Point);
        backend.Text = backend.Text! with { Start = 2, End = 15 };
        Assert.False(reader.Validate(result));
        backend.Text = new(1, 7, 20, "selected text"); backend.Window = new(200, 43);
        Assert.False(reader.Validate(result));
        backend.Window = new(100, 42); reader.SetEnabled(false, 4);
        Assert.False(reader.Validate(result)); Assert.Null(reader.Read(new(3)));
        reader.SetEnabled(true, 5); Assert.False(reader.Validate(result));
    }

    [Fact]
    public void Reader_drops_a_result_when_mode_changes_inside_a_backend_call()
    {
        var backend = new FakeBackend();
        using var reader = new JavaSelectionReader(backend);
        reader.SetEnabled(true, 1);
        backend.DuringRead = () => reader.SetEnabled(true, 2);
        Assert.Null(reader.Read(new(1)));
    }

    [Fact]
    public void Missing_bridge_explanation_is_available_to_the_monitor()
    {
        var backend = new FakeBackend { Text = null, UnavailableReason = "Enable Java Access Bridge" };
        using var reader = new JavaSelectionReader(backend);
        reader.SetEnabled(true, 1);
        Assert.Null(reader.Read(new(1))); Assert.Equal(backend.UnavailableReason, reader.UnavailableReason);
        backend.Window = null;
        Assert.Null(reader.Read(new(1))); Assert.Null(reader.UnavailableReason);
    }

    [Fact]
    public void Java_and_windows_selection_evidence_is_validated_by_its_original_reader()
    {
        var windows = new FakeReader { Result = new(42, 100, "terminal", "terminal text", "1", "64", new object()) };
        var java = new FakeReader { Result = new(42, 100, "java", "java text", "2", "64", new object()) };
        using var reader = new DesktopSelectionReader(windows, java);
        Assert.True(reader.Start()); reader.SetEnabled(true, 1);
        var first = reader.Read(new(1)); Assert.NotNull(first); Assert.Equal("java text", first.Text);
        Assert.True(reader.Validate(first)); Assert.Equal(1, java.Validations); Assert.Equal(0, windows.Validations);
        java.Result = null;
        var second = reader.Read(new(1)); Assert.NotNull(second); Assert.Equal("terminal text", second.Text);
        Assert.True(reader.Validate(second)); Assert.Equal(1, windows.Validations);
        Assert.False(reader.Validate(first)); // Cannot validate old Java text using the Windows fallback.
    }

    [Fact]
    public void Native_abi_layout_matches_the_documented_x64_bridge_without_loading_a_dll()
    {
        var assembly = typeof(JavaSelectionReader).Assembly;
        var context = assembly.GetType("PinyinTranslate.Selection.WindowsJavaSelectionBackend+NativeAccess+ContextInfo", true)!;
        var selection = assembly.GetType("PinyinTranslate.Selection.WindowsJavaSelectionBackend+NativeAccess+SelectionInfo", true)!;
        Assert.Equal(6188, Marshal.SizeOf(context)); Assert.Equal(2056, Marshal.SizeOf(selection));
        Assert.Equal(4608, Marshal.OffsetOf(context, "RoleEnglish").ToInt32());
        Assert.Equal(6180, Marshal.OffsetOf(context, "AccessibleText").ToInt32());
    }

    private sealed class FakeReader : ISelectionReader
    {
        public SelectionReadResult? Result;
        public int Validations;
        public event Action<SelectionReaderSignal>? Changed { add { } remove { } }
        public bool Start() => true;
        public void SetEnabled(bool enabled, long epoch) { }
        public SelectionReadResult? Read(SelectionReadRequest request) => Result;
        public bool Validate(SelectionReadResult selection) { Validations++; return selection == Result; }
        public void Dispose() { }
    }

    private sealed class FakeBackend : IJavaSelectionBackend
    {
        public JavaSelectionWindow? Window = new(100, 42);
        public JavaSelectedText? Text = new(1, 7, 20, "selected text");
        public string? UnavailableReason { get; set; }
        public (int?, int?) Point;
        public Action? DuringRead;
        public JavaSelectionWindow? GetForegroundJavaWindow() => Window;
        public JavaSelectedText? Read(JavaSelectionWindow window, int? x, int? y, Func<bool> allowed)
        { Point = (x, y); DuringRead?.Invoke(); return allowed() ? Text : null; }
        public void Dispose() { }
    }

    private sealed class FakeAccess : IJavaTextAccess
    {
        private readonly Dictionary<long, int> references = new();
        private long next = 100;
        public int Outstanding => references.Count;
        public int EditorIdentity = 1;
        public bool RootAvailable = true;
        public JavaContextInfo Editor = new("text", "enabled, visible, showing, editable", true, new(0, 0, 500, 500));
        public JavaContextInfo Root = new("frame", "visible, showing", false, new(0, 0, 1000, 1000));
        public string Document = "prefix selected text suffix";
        public (int Start, int End) Range = (7, 20);
        public List<(int, int)> ReadRanges = [];
        public Action? BeforeReadText;
        private JavaContext Acquire(int logical) { references.Add(++next, logical); return new(42, next); }
        public JavaContext? GetRoot(nint window) => RootAvailable ? Acquire(0) : null;
        public JavaContext? GetFocus(nint window) => Acquire(EditorIdentity);
        public JavaContext? GetAt(JavaContext root, int x, int y) => Acquire(EditorIdentity);
        public JavaContext? GetParent(JavaContext context) => references[context.ObjectId] == 0 ? null : Acquire(0);
        public JavaContextInfo? GetInfo(JavaContext context) => references[context.ObjectId] == 0 ? Root : Editor;
        public (int Start, int End)? GetSelection(JavaContext context) => Range;
        public string? GetText(JavaContext context, int start, int end)
        { ReadRanges.Add((start, end)); BeforeReadText?.Invoke(); return Document[start..end]; }
        public bool IsSame(JavaContext left, JavaContext right) => left.VmId == right.VmId && references[left.ObjectId] == references[right.ObjectId];
        public void Release(JavaContext context) => Assert.True(references.Remove(context.ObjectId));
    }
}
