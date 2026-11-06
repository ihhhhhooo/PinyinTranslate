using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

public sealed class EditableContextAnchorTests
{
    [Theory]
    [InlineData("original suffix", 9, "original s'suffix", 11, "s'")]
    [InlineData("", 0, "ni'hao", 6, "ni'hao")]
    [InlineData("A😀B", 3, "A😀shiB", 6, "shi")]
    public void MatchesOnlyAsciiInsertionAtOriginalCaret(string original, int caret, string live, int liveCaret, string expected)
    {
        Assert.True(BaselineAnchor.TryCreate(original, caret, caret, out var anchor));
        Assert.True(anchor!.TryMatch(live, liveCaret, liveCaret, out var composition));
        Assert.Equal(expected, composition);
    }

    [Theory]
    [InlineData("left RIGHT", 5, "left sWRONG", 6, 6)]
    [InlineData("left RIGHT", 5, "Left sRIGHT", 6, 6)]
    [InlineData("left RIGHT", 5, "left 是RIGHT", 6, 6)]
    [InlineData("left RIGHT", 5, "left s RIGHT", 7, 7)]
    [InlineData("left RIGHT", 5, "left sRIGHT", 5, 5)]
    [InlineData("left RIGHT", 5, "left sRIGHT", 5, 6)]
    [InlineData("left RIGHT", 5, "left RIGHT", 5, 5)]
    public void RejectsChangedContextNonPinyinWrongCaretAndSelection(string original, int caret, string live, int start, int end)
    {
        Assert.True(BaselineAnchor.TryCreate(original, caret, caret, out var anchor));
        Assert.False(anchor!.TryMatch(live, start, end, out _));
    }

    [Fact]
    public void RejectsNonCollapsedAndSplitSurrogateBaseline()
    {
        Assert.False(BaselineAnchor.TryCreate("abc", 1, 2, out _));
        Assert.False(BaselineAnchor.TryCreate("A😀B", 2, 2, out _));
        Assert.False(BaselineAnchor.TryCreate(new string('a', 8193), 0, 0, out _));
    }

    [Fact]
    public void SnapshotMatchRequiresProfileAndSameContext()
    {
        var baseline = new EditableContextSnapshot
        {
            ForegroundWindow = 1, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "1.2.3",
            FrameworkId = "WPF", ClassName = "TextBox", Value = "left RIGHT", DocumentText = "left RIGHT",
            SelectionStartUtf16 = 5, SelectionEndUtf16 = 5, CanReplace = true
        };
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var live = baseline with { Value = "left sRIGHT", DocumentText = "left sRIGHT", SelectionStartUtf16 = 6, SelectionEndUtf16 = 6 };
        Assert.True(anchor!.TryMatch(live, out _));
        Assert.False(anchor.TryMatch(live with { FocusRuntimeId = "other" }, out _));
        Assert.False(anchor.TryMatch(live with { CanReplace = false }, out _));
        Assert.False(anchor.TryMatch(live with { DocumentText = "different" }, out _));
    }
}
