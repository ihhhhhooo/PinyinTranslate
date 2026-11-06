using PinyinTranslate.Selection;
namespace PinyinTranslate.Selection.Tests;
public class SelectionGuardTests
{
    [Fact] public void BackgroundOrUnknownEventSourceCannotMatchFocusedElement()
    {
        Assert.True(SelectionGuard.EventSourceMatches(20, "42.1.9", 20, "42.1.9"));
        Assert.False(SelectionGuard.EventSourceMatches(20, "42.1.9", 21, "42.1.9"));
        Assert.False(SelectionGuard.EventSourceMatches(20, "42.1.9", 20, "42.1.10"));
        Assert.False(SelectionGuard.EventSourceMatches(20, null, 20, null));
        Assert.False(SelectionGuard.EventSourceMatches(0, "42", 0, "42"));
    }
    [Fact] public void OnlyExternalNonProtectedForegroundSelectionIsEligible()
    {
        Assert.True(SelectionGuard.Allows(10, 20, false, true, 100, 100));
        Assert.False(SelectionGuard.Allows(10, 10, false, true, 100, 100));
        Assert.False(SelectionGuard.Allows(10, 20, true, true, 100, 100));
        Assert.False(SelectionGuard.Allows(10, 20, false, false, 100, 100));
        Assert.False(SelectionGuard.Allows(10, 20, false, true, 100, 101));
        Assert.False(SelectionGuard.Allows(10, 20, false, true, 0, 0));
        Assert.False(SelectionGuard.Allows(10, 0, false, true, 100, 100));
    }
}
