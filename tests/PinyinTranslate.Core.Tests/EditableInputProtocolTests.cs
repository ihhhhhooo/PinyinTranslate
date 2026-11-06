using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

public sealed class EditableInputProtocolTests
{
    [Theory]
    [InlineData("WinForm", "WindowsForms10.Edit.app.0.2360855_r3_ad1", true)]
    [InlineData("WinForm", "WindowsForms10.Edit.app.0.abcdef_r9_ad2", true)]
    [InlineData("Win32", "WindowsForms10.Edit.app.0.2360855_r3_ad1", false)]
    [InlineData("WPF", "WindowsForms10.Edit.app.0.2360855_r3_ad1", false)]
    [InlineData("WinForm", "WindowsForms10.Editable.app.0.2360855_r3_ad1", false)]
    [InlineData("WinForm", "WindowsForms10.RICHEDIT50W.app.0.2360855_r3_ad1", false)]
    [InlineData("WinForm", "prefix.WindowsForms10.Edit.app.0.2360855_r3_ad1", false)]
    public void PlainWinFormsProfileRequiresFrameworkAndKnownClassPrefix(string framework, string className, bool expected) =>
        Assert.Equal(expected, Matches(TextInputProfile.WinFormsEdit, framework, className));

    [Theory]
    [InlineData("WinForm", "WindowsForms10.RICHEDIT50W.app.0.2360855_r3_ad1", true)]
    [InlineData("WinForm", "WindowsForms10.RICHEDIT50W.app.0.abcdef_r9_ad2", true)]
    [InlineData("Win32", "WindowsForms10.RICHEDIT50W.app.0.2360855_r3_ad1", false)]
    [InlineData("WinForm", "WindowsForms10.RICHEDIT50WA.app.0.2360855_r3_ad1", false)]
    [InlineData("WinForm", "WindowsForms10.Edit.app.0.2360855_r3_ad1", false)]
    public void RichWinFormsProfileDoesNotBroadenToOtherFrameworksOrControls(string framework, string className, bool expected) =>
        Assert.Equal(expected, Matches(TextInputProfile.WinFormsRichEdit, framework, className));

    [Fact]
    public void OrdinaryProfilesRemainExactAndCannotOptIntoArbitraryPrefixRules()
    {
        var profile = new TextInputProfile("Win32", "Edit");
        Assert.True(Matches(profile, "Win32", "Edit"));
        Assert.False(Matches(profile, "Win32", "EditExtra"));
        Assert.False(Matches(profile, "Win32", "edit"));
        Assert.Throws<ArgumentException>(() => new EditableContextAdapter([
            profile with { AllowWinFormsRuntimeSuffix = true }
        ]));
        Assert.Throws<ArgumentException>(() => new EditableContextAdapter([
            profile with { AllowRichEditValueTerminator = true }
        ]));
    }

    [Theory]
    [InlineData("PREFIX | SUFFIX", true)]
    [InlineData("PREFIX | SUFFIX\r", true)]
    [InlineData("PREFIX | SUFFIX\r\n", true)]
    [InlineData("PREFIX | SUFFIX\0", true)]
    [InlineData("PREFIX | SUFFIX\n", false)]
    [InlineData("PREFIX | SUFFIX\r\r", false)]
    [InlineData("PREFIX | SUFFIX!", false)]
    [InlineData("PREFIX\r | SUFFIX", false)]
    [InlineData("prefix | SUFFIX\r", false)]
    public void RichEditCompatibilityOnlyNormalizesOneKnownTerminal(string value, bool expected) =>
        Assert.Equal(expected, Invoke<bool>("HasCompatibleRichEditTerminator", value, "PREFIX | SUFFIX"));

    [Theory]
    [InlineData("hello")]
    [InlineData("don't change the suffix")]
    [InlineData("你好，world")]
    [InlineData("emoji 😀 and combining e\u0301")]
    public void UnicodeInsertionAllowsWellFormedPrintableUtf16(string text) =>
        Assert.True(Invoke<bool>("IsSupportedUnicodeText", text));

    [Theory]
    [InlineData("line\nnext")]
    [InlineData("line\rnext")]
    [InlineData("tab\tcommand")]
    [InlineData("escape\u001bcommand")]
    [InlineData("null\0tail")]
    [InlineData("delete\u007f")]
    public void UnicodeInsertionRejectsControlKeysBeforeAnyNativeInput(string text) =>
        Assert.False(Invoke<bool>("IsSupportedUnicodeText", text));

    [Fact]
    public void UnicodeInsertionRejectsIsolatedSurrogates()
    {
        // Keep malformed strings inside the test: test discovery serializes
        // member data as Unicode and can replace the very units being tested.
        string[] malformed = [new('\uD800', 1), new('\uDC00', 1), "x" + new string('\uD800', 1) + "y",
            new(['\uD800', '\uD800']), new(['\uDC00', '\uD800'])];
        foreach (string text in malformed) Assert.False(Invoke<bool>("IsSupportedUnicodeText", text));
    }

    [Fact]
    public void NativeInputMatchesWindowsAbiAndTagsBothKeyboardTransitions()
    {
        Type inputType = Nested("NativeInput");
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(inputType));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf(inputType, "Data").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf(Nested("NativeKeyboardInput")));
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf(Nested("NativeMouseInput")));
        foreach (uint flags in new uint[] { 0x0004, 0x0004 | 0x0002 })
        {
            object input = Invoke<object>("KeyboardInput", (ushort)0, (ushort)'a', flags);
            Assert.Equal(1u, inputType.GetField("Type")!.GetValue(input));
            object union = inputType.GetField("Data")!.GetValue(input)!;
            object keyboard = union.GetType().GetField("Keyboard")!.GetValue(union)!;
            Assert.Equal(InputKeyboardHook.SyntheticInputMarker, keyboard.GetType().GetField("ExtraInfo")!.GetValue(keyboard));
        }
    }

    [Fact]
    public void TextPatternOnlySnapshotsKeepTheExactBaselineAndCaretGuard()
    {
        var baseline = new EditableContextSnapshot
        {
            ForegroundWindow = 1, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "1.2.3",
            FrameworkId = "Win32", ClassName = "VerifiedEdit", HasTextPattern = true, HasValuePattern = false,
            DocumentText = "PREFIX | SUFFIX", HasSingleSelection = true, SelectionStartUtf16 = 7, SelectionEndUtf16 = 7,
            CanReplace = true, IsReadOnly = false, CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
        };
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var composing = baseline with { DocumentText = "PREFIX shi| SUFFIX", SelectionStartUtf16 = 10, SelectionEndUtf16 = 10 };
        Assert.True(anchor!.TryMatch(composing, out var raw));
        Assert.Equal("shi", raw);
        Assert.False(anchor.TryMatch(composing with { FocusRuntimeId = "different" }, out _));
        Assert.False(anchor.TryMatch(composing with { SelectionEndUtf16 = 11 }, out _));
        Assert.False(anchor.TryMatch(composing with { DocumentText = "PREFIX shi| WRONG" }, out _));
    }

    [Fact]
    public void PostEscapeCheckRequiresExactTextCollapsedCaretAndEnabledProfile()
    {
        var snapshot = new EditableContextSnapshot
        {
            HasTextPattern = true, HasValuePattern = true, Value = "PREFIX | SUFFIX\r", DocumentText = "PREFIX | SUFFIX",
            UsesDocumentText = true, HasSingleSelection = true, SelectionStartUtf16 = 7, SelectionEndUtf16 = 7,
            CanReplace = true, IsReadOnly = false, CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
        };
        bool MatchesExact(EditableContextSnapshot candidate) => Invoke<bool>("IsExactText", candidate, "PREFIX | SUFFIX", 7);
        Assert.True(MatchesExact(snapshot));
        Assert.False(MatchesExact(snapshot with { DocumentText = "PREFIX s| SUFFIX" }));
        Assert.False(MatchesExact(snapshot with { SelectionStartUtf16 = 8, SelectionEndUtf16 = 8 }));
        Assert.False(MatchesExact(snapshot with { SelectionEndUtf16 = 8 }));
        Assert.False(MatchesExact(snapshot with { IsPassword = true }));
        Assert.False(MatchesExact(snapshot with { IsReadOnly = true }));
        Assert.False(MatchesExact(snapshot with { CanReplace = false }));
        Assert.False(MatchesExact(snapshot with { CommitMethod = EditableCommitMethod.None }));
    }

    [Fact]
    public void PostEscapeValueWriteIsLimitedToWritablePlainWpfTextBox()
    {
        var baseline = new EditableContextSnapshot
        {
            FrameworkId = "WPF", ClassName = "TextBox", HasTextPattern = true, HasValuePattern = true,
            Value = "PREFIX | SUFFIX", DocumentText = "PREFIX | SUFFIX", HasSingleSelection = true,
            SelectionStartUtf16 = 7, SelectionEndUtf16 = 7, IsReadOnly = false, CanReplace = true,
            CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
        };
        bool UsesValue(EditableContextSnapshot candidate) => Invoke<bool>("ShouldUsePostEscapeValuePattern", candidate);
        Assert.True(UsesValue(baseline));
        Assert.False(UsesValue(baseline with { FrameworkId = "WinForm", ClassName = "WindowsForms10.Edit.app.0.test" }));
        Assert.False(UsesValue(baseline with { FrameworkId = "WinForm", ClassName = "WindowsForms10.RICHEDIT50W.app.0.test" }));
        Assert.False(UsesValue(baseline with { ClassName = "RichTextBox" }));
        Assert.False(UsesValue(baseline with { HasValuePattern = false }));
        Assert.False(UsesValue(baseline with { PreferGenericPaste = true }));
        Assert.False(UsesValue(baseline with { IsReadOnly = true }));
        Assert.False(UsesValue(baseline with { IsPassword = true }));
        Assert.False(UsesValue(baseline with { CanReplace = false }));
        Assert.False(UsesValue(baseline with { DocumentText = "different" }));
    }

    [Theory]
    [InlineData(EditableCommitMethod.ValuePattern)]
    [InlineData(EditableCommitMethod.CancelCompositionAndUnicode)]
    public void Dedicated_commit_cannot_silently_continue_after_the_live_provider_requires_generic_paste(EditableCommitMethod method)
    {
        var expected = new EditableContextSnapshot { CommitMethod = method, HasValuePattern = true, Value = "same", DocumentText = "same" };
        bool Stable(EditableContextSnapshot live) => Invoke<bool>("IsDedicatedCommitStable", expected, live);
        Assert.True(Stable(expected));
        Assert.False(Stable(expected with { PreferGenericPaste = true }));
        Assert.False(Stable(expected with { CommitMethod = EditableCommitMethod.CancelCompositionAndPaste }));
        Assert.False(Stable(expected with { CommitMethod = EditableCommitMethod.None }));
        if (method == EditableCommitMethod.ValuePattern) Assert.False(Stable(expected with { Value = "conflicting" }));
    }

    [Fact]
    public void WrittenValueCaretCompletionOnlyAcceptsTheKnownResetOrAlreadyCorrectCollapsedCaret()
    {
        var written = new EditableContextSnapshot
        {
            FrameworkId = "WPF", ClassName = "TextBox", HasTextPattern = true, HasValuePattern = true,
            Value = "PREFIX belong to| SUFFIX", DocumentText = "PREFIX belong to| SUFFIX", HasSingleSelection = true,
            SelectionStartUtf16 = 0, SelectionEndUtf16 = 0, IsReadOnly = false, CanReplace = true,
            CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
        };
        bool MayComplete(EditableContextSnapshot candidate) => Invoke<bool>("CanFinishPostValueCaret", candidate, written.Value, 16);
        Assert.True(MayComplete(written));
        Assert.True(MayComplete(written with { SelectionStartUtf16 = 16, SelectionEndUtf16 = 16 }));
        Assert.False(MayComplete(written with { SelectionStartUtf16 = 1, SelectionEndUtf16 = 1 }));
        Assert.False(MayComplete(written with { SelectionEndUtf16 = 1 }));
        Assert.False(MayComplete(written with { HasSingleSelection = false }));
        Assert.False(MayComplete(written with { Value = "user changed the text" }));
        Assert.False(MayComplete(written with { DocumentText = "user changed the text" }));
        Assert.False(MayComplete(written with { IsReadOnly = true }));
        Assert.False(MayComplete(written with { IsPassword = true }));
        Assert.False(MayComplete(written with { FrameworkId = "WinForm" }));
    }

    [Fact]
    public void IndependentCaretPermissionSurvivesOrdinaryCancellationButRejectsLocationIntentChange()
    {
        // No UIA worker or native API is used: only the independent permission
        // branch is invoked on an uninitialized adapter with explicit delegates.
        var adapter = (EditableContextAdapter)RuntimeHelpers.GetUninitializedObject(typeof(EditableContextAdapter));
        var permission = typeof(EditableContextAdapter).GetMethod("IsCaretCompletionAuthorized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Authorize(Func<bool>? locationPermission) => (bool)permission.Invoke(adapter,
            [new CancellationToken(true), (Func<bool>)(() => false), locationPermission])!;
        Assert.True(Authorize(() => true));
        Assert.False(Authorize(() => false));
        Assert.False(Authorize(() => throw new InvalidOperationException("location unavailable")));
        Assert.False(Authorize(null));
        typeof(EditableContextAdapter).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(adapter, 1);
        Assert.False(Authorize(() => true));
    }

    [Fact]
    public void VerifiedNativeOverlayMatchesOnlyTheExactOriginalDocumentAndCaret()
    {
        var baseline = new EditableContextSnapshot
        {
            ForegroundWindow = 1, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "native-runtime",
            FrameworkId = "WinForm", ClassName = "WindowsForms10.Edit.app.0.test", HasTextPattern = true, HasValuePattern = true,
            Value = "PREFIX | SUFFIX", DocumentText = "PREFIX | SUFFIX", HasSingleSelection = true,
            SelectionStartUtf16 = 7, SelectionEndUtf16 = 7, IsReadOnly = false, CanReplace = true,
            CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode, AllowsOverlayComposition = true
        };
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        Assert.True(anchor!.TryMatch(baseline, out var overlayText));
        Assert.Equal("", overlayText);
        Assert.False(anchor.TryMatch(baseline with { AllowsOverlayComposition = false }, out _));
        Assert.False(anchor.TryMatch(baseline with { CanReplace = false }, out _));
        Assert.False(anchor.TryMatch(baseline with { FocusRuntimeId = "other" }, out _));
        Assert.False(anchor.TryMatch(baseline with { Value = "DIFFERENT", DocumentText = "DIFFERENT" }, out _));
        Assert.False(anchor.TryMatch(baseline with { SelectionStartUtf16 = 8, SelectionEndUtf16 = 8 }, out _));
        Assert.False(anchor.TryMatch(baseline with { SelectionEndUtf16 = 8 }, out _));
        Assert.False(anchor.TryMatch(baseline with { HasSingleSelection = false }, out _));
        Assert.False(anchor.TryMatch(baseline with { IsReadOnly = true }, out _));
        Assert.False(anchor.TryMatch(baseline with { IsPassword = true }, out _));
        var inline = baseline with { Value = "PREFIX shi| SUFFIX", DocumentText = "PREFIX shi| SUFFIX", SelectionStartUtf16 = 10, SelectionEndUtf16 = 10 };
        Assert.True(anchor.TryMatch(inline, out var inlineText));
        Assert.Equal("shi", inlineText);
    }

    [Fact]
    public void OrdinaryInlineProfilesStillRequireAnAsciiInsertionAndRejectAnEmptyDifference()
    {
        var baseline = new EditableContextSnapshot
        {
            ForegroundWindow = 1, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "wpf-runtime",
            FrameworkId = "WPF", ClassName = "TextBox", HasTextPattern = true, HasValuePattern = true,
            Value = "PREFIX | SUFFIX", DocumentText = "PREFIX | SUFFIX", HasSingleSelection = true,
            SelectionStartUtf16 = 7, SelectionEndUtf16 = 7, IsReadOnly = false, CanReplace = true,
            CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
        };
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        Assert.False(anchor!.TryMatch(baseline, out _));
        var inline = baseline with { Value = "PREFIX shu'yu| SUFFIX", DocumentText = "PREFIX shu'yu| SUFFIX", SelectionStartUtf16 = 13, SelectionEndUtf16 = 13 };
        Assert.True(anchor.TryMatch(inline, out var raw));
        Assert.Equal("shu'yu", raw);
        Assert.False(anchor.TryMatch(inline with { Value = "PREFIX 属于| SUFFIX", DocumentText = "PREFIX 属于| SUFFIX", SelectionStartUtf16 = 9, SelectionEndUtf16 = 9 }, out _));
    }

    private static bool Matches(TextInputProfile profile, string framework, string className) =>
        (bool)typeof(TextInputProfile).GetMethod("Matches", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profile, [framework, className])!;
    private static Type Nested(string name) => typeof(EditableContextAdapter).GetNestedType(name, BindingFlags.NonPublic)!;
    private static T Invoke<T>(string method, params object[] arguments) =>
        (T)typeof(EditableContextAdapter).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
}
