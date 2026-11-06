using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

public class InputSessionTests
{
    private static InputSnapshot Snapshot(string candidate = "甲", string caret = "0", string focus = "a") =>
        new(123, 456, focus, "composition", "page", candidate, caret);

    [Fact] public void AbaDoesNotResurrectEarlierTicket()
    {
        var session = new InputSession(); session.SetEnabled(true);
        var a = session.Observe(Snapshot())!.Value;
        session.Observe(Snapshot("乙"));
        var nextA = session.Observe(Snapshot())!.Value;
        Assert.False(session.IsCurrent(a, Snapshot()));
        Assert.True(session.IsCurrent(nextA, Snapshot()));
    }

    [Fact] public void ChangedFocusOrCaretIsRejected()
    {
        var session = new InputSession(); session.SetEnabled(true);
        var ticket = session.Observe(Snapshot())!.Value;
        Assert.False(session.IsCurrent(ticket, Snapshot(caret: "1")));
        Assert.False(session.IsCurrent(ticket, Snapshot(focus: "b")));
    }

    [Fact] public void DisableInvalidatesEvenIfReenabled()
    {
        var session = new InputSession(); session.SetEnabled(true);
        var ticket = session.Observe(Snapshot())!.Value;
        session.SetEnabled(false); session.SetEnabled(true);
        Assert.False(session.IsCurrent(ticket, Snapshot()));
        Assert.False(ImeInputCapability.CanSafelyInterceptAndInsert);
    }
}
