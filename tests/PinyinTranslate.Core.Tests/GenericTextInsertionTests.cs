using System.Reflection;
using System.Runtime.InteropServices;
using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Only fakes and pure event-plan builders are used. These tests never construct
// a native session, access the clipboard, create a window, or send keyboard input.
[Trait("Category", "HeadlessOnly")]
public sealed class GenericTextInsertionTests
{
    [Fact]
    public void Metadata_progress_distinguishes_clipboard_preparation_dispatch_verification_and_restore_without_text()
    {
        var test = new Fixture();
        var progress = new List<ImeWriteProgress>();
        test.Backend.WriteProgress += progress.Add;
        var result = test.Backend.Insert("synthetic-private-translation", () => true, () => true, default);
        Assert.True(result.Verified);
        Assert.Equal(new[] { "clipboard", "clipboard", "insertion", "verification", "clipboard-restore", "clipboard-restore" },
            progress.Select(item => item.Stage));
        Assert.Equal("Preparing", progress[0].Code);
        Assert.Equal("Prepared", progress[1].Code);
        Assert.Equal("PasteDispatch", progress[2].Code);
        Assert.All(progress, item => Assert.DoesNotContain("synthetic-private-translation", item.Code));
    }

    [Fact]
    public void VerifiedPasteRestoresClipboardAfterVerificationAndNeverFallsBack()
    {
        var test = new Fixture();
        var result = test.Backend.Insert("belong to", () => true, () => { test.Events.Add("verify"); return true; }, default);
        Assert.True(result.InputAttempted); Assert.True(result.AnyInputSent); Assert.True(result.Verified);
        Assert.Equal(ClipboardRestorationStatus.Restored, result.ClipboardRestoration);
        Assert.Equal(new[] { "prepare", "paste", "verify", "restore", "dispose" }, test.Events);
        Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void AuthorizationAndVerificationStayOnTheCallerAndAuthorizationIsNeverReusedAfterInput()
    {
        var test = new Fixture();
        int caller = Environment.CurrentManagedThreadId, checks = 0;
        bool Authorize()
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            Assert.False(test.Keyboard.InputAttempted);
            checks++;
            return true;
        }
        var result = test.Backend.Insert("hello", Authorize, () =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            Assert.True(test.Keyboard.InputAttempted);
            return true;
        }, default);
        Assert.True(result.Verified); Assert.True(checks >= 2);
        Assert.Equal(caller, test.Clipboard.CallerThread);
    }

    [Fact]
    public void UnsupportedClipboardPreparationFallsBackToUnicodeExactlyOnce()
    {
        var test = new Fixture();
        test.Clipboard.Prepared = false;
        var result = test.Backend.Insert("hello", () => true, () => true, default);
        Assert.True(result.Verified); Assert.True(result.InputAttempted);
        Assert.Equal(0, test.Keyboard.PasteCalls); Assert.Equal(1, test.Keyboard.UnicodeCalls);
        Assert.Equal("hello", test.Keyboard.UnicodeText);
        Assert.Equal(ClipboardRestorationStatus.NotUsed, result.ClipboardRestoration);
    }

    [Fact]
    public void ClipboardPreparationExceptionCanFallBackBecauseNoKeyboardWasAttempted()
    {
        var test = new Fixture();
        test.Clipboard.ThrowOnPrepare = true;
        var result = test.Backend.Insert("hello", () => true, () => true, default);
        Assert.True(result.Verified);
        Assert.Equal(0, test.Keyboard.PasteCalls); Assert.Equal(1, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void InitialCancellationDoesNotTouchEitherTransport()
    {
        var test = new Fixture();
        var result = test.Backend.Insert("hello", () => true, () => throw new Exception("must not verify"), new CancellationToken(true));
        Assert.False(result.InputAttempted); Assert.Empty(test.Events);
        Assert.Equal(0, test.Clipboard.PrepareCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void RevokedAuthorizationAfterPreparationRestoresWithoutInputOrFallback()
    {
        var test = new Fixture();
        bool authorized = true;
        test.Clipboard.OnPrepare = () => authorized = false;
        var result = test.Backend.Insert("hello", () => authorized, () => throw new Exception("must not verify"), default);
        Assert.False(result.InputAttempted); Assert.False(result.Verified);
        Assert.Equal(1, test.Session.RestoreCalls); Assert.Equal(1, test.Session.DisposeCalls);
        Assert.Equal(0, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void LastKeyboardGuardCanRejectWithoutTriggeringUnicodeFallback()
    {
        var test = new Fixture();
        int checks = 0;
        var result = test.Backend.Insert("hello", () => ++checks < 3, () => throw new Exception("must not verify"), default);
        Assert.False(result.InputAttempted); Assert.False(result.AnyInputSent);
        Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal(1, test.Session.RestoreCalls);
    }

    [Fact]
    public void ClipboardReplacementAfterPreparationCancelsBeforeAnyKeyboardCall()
    {
        var test = new Fixture();
        test.Clipboard.OnPrepare = () => test.Session.UserCopy("new user clipboard");
        var result = test.Backend.Insert("hello", () => true, () => throw new Exception("must not verify"), default);
        Assert.False(result.InputAttempted); Assert.False(result.AnyInputSent);
        Assert.Equal(0, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal("new user clipboard", test.Session.CurrentText);
        Assert.Equal(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, result.ClipboardRestoration);
    }

    [Fact]
    public void ClipboardReplacementAtTheLastKeyboardGuardCannotPasteOtherContentOrFallBack()
    {
        var test = new Fixture();
        test.Keyboard.BeforePasteGuard = () => test.Session.UserCopy("new user clipboard");
        var result = test.Backend.Insert("hello", () => true, () => throw new Exception("must not verify"), default);
        Assert.False(result.InputAttempted); Assert.False(result.AnyInputSent); Assert.False(test.Keyboard.InputAttempted);
        Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal("new user clipboard", test.Session.CurrentText);
        Assert.Equal(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, result.ClipboardRestoration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialOrRejectedPasteNeverRetriesAndNeverRestoresWhilePasteCouldBePending(int sent)
    {
        var test = new Fixture();
        test.Keyboard.PasteResult = new(true, 4, (uint)sent, false, "partial");
        int verified = 0;
        var result = test.Backend.Insert("hello", () => true, () => { verified++; return true; }, default);
        Assert.True(result.InputAttempted); Assert.Equal(sent > 0, result.AnyInputSent); Assert.False(result.Verified);
        Assert.Equal(0, verified); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal(sent == 0 ? 1 : 0, test.Session.RestoreCalls);
        Assert.Equal(sent == 0 ? ClipboardRestorationStatus.Restored : ClipboardRestorationStatus.RetainedForUnconfirmedPaste, result.ClipboardRestoration);
    }

    [Fact]
    public void UncertainPasteDispatchKeepsTranslationAndDoesNotRetry()
    {
        var test = new Fixture();
        test.Keyboard.ThrowOnPaste = true;
        var result = test.Backend.Insert("hello", () => true, () => throw new Exception("must not verify"), default);
        Assert.True(result.InputAttempted); Assert.True(result.AnyInputSent); Assert.False(result.Verified);
        Assert.Equal(ClipboardRestorationStatus.RetainedForUnconfirmedPaste, result.ClipboardRestoration);
        Assert.Equal(0, test.Session.RestoreCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal(1, test.Session.DisposeCalls);
    }

    [Fact]
    public void CompletedButUnverifiedPasteRetainsTranslationForALatePaste()
    {
        var test = new Fixture();
        var result = test.Backend.Insert("hello", () => true, () => false, default);
        Assert.True(result.InputAttempted); Assert.True(result.AnyInputSent); Assert.False(result.Verified);
        Assert.Equal(ClipboardRestorationStatus.RetainedForUnconfirmedPaste, result.ClipboardRestoration);
        Assert.Equal(0, test.Session.RestoreCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void VerificationExceptionCannotCauseRestoreOrAnotherInsertion()
    {
        var test = new Fixture();
        var result = test.Backend.Insert("hello", () => true, () => throw new InvalidOperationException(), default);
        Assert.False(result.Verified); Assert.Equal(0, test.Session.RestoreCalls);
        Assert.Equal(0, test.Keyboard.UnicodeCalls); Assert.Equal(1, test.Keyboard.PasteCalls);
    }

    [Fact]
    public void CancellationAfterDispatchStillAllowsVerificationOfTheAlreadySentPaste()
    {
        var test = new Fixture();
        using var cancellation = new CancellationTokenSource();
        test.Keyboard.OnPaste = cancellation.Cancel;
        int verifications = 0;
        var result = test.Backend.Insert("hello", () => true, () => { verifications++; return true; }, cancellation.Token);
        Assert.True(result.Verified); Assert.Equal(1, verifications); Assert.Equal(1, test.Session.RestoreCalls);
        Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void NewUserClipboardContentIsPreservedWhenConditionalRestoreDeclines()
    {
        var test = new Fixture();
        test.Session.RestoreResult = new(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, "new clipboard content preserved");
        var result = test.Backend.Insert("hello", () => true, () => true, default);
        Assert.True(result.Verified);
        Assert.Equal(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, result.ClipboardRestoration);
        Assert.Equal(1, test.Session.RestoreCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void RestoreFailureDoesNotTurnVerifiedPasteIntoAnotherInsertion()
    {
        var test = new Fixture();
        test.Session.ThrowOnRestore = true;
        var result = test.Backend.Insert("hello", () => true, () => true, default);
        Assert.True(result.Verified); Assert.Equal(ClipboardRestorationStatus.Failed, result.ClipboardRestoration);
        Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void UnicodeFallbackFailureIsTerminalAndKeepsPreparationRestorationStatus()
    {
        var test = new Fixture();
        test.Clipboard.Prepared = false;
        test.Clipboard.FailedRestoration = ClipboardRestorationStatus.Failed;
        test.Keyboard.UnicodeResult = new(true, 10, 3, false, "partial unicode");
        var result = test.Backend.Insert("hello", () => true, () => throw new Exception("must not verify"), default);
        Assert.True(result.InputAttempted); Assert.True(result.AnyInputSent); Assert.False(result.Verified);
        Assert.Equal(1, test.Keyboard.UnicodeCalls); Assert.Equal(0, test.Keyboard.PasteCalls);
        Assert.Equal(ClipboardRestorationStatus.Failed, result.ClipboardRestoration);
    }

    [Fact]
    public void InvalidTextNeverReachesEitherTransport()
    {
        string?[] invalid = [null, "", "a\0b", new string('\ud800', 1), new string('\udc00', 1), new string('x', GenericTextInsertionBackend.MaximumTextLength + 1)];
        foreach (string? text in invalid)
        {
            var test = new Fixture();
            var result = test.Backend.Insert(text!, () => true, () => true, default);
            Assert.False(result.InputAttempted); Assert.Empty(test.Events);
        }
    }

    [Fact]
    public void ClipboardPrimaryCanCarryMultilineTextWithoutUnicodeControlKeyFallback()
    {
        var test = new Fixture();
        var result = test.Backend.Insert("one\r\ntwo\tthree", () => true, () => true, default);
        Assert.True(result.Verified); Assert.Equal("one\r\ntwo\tthree", test.Clipboard.PreparedText);
        Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void PasteAndPartialCleanupPlansHaveTheNativeAbiAndOnlyOurMarkedKeys()
    {
        Type sender = typeof(WindowsTextInputSender);
        Type inputType = sender.GetNestedType("NativeInput", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(inputType));
        var paste = (Array)sender.GetMethod("CreatePastePlan", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Assert.Equal(4, paste.Length);
        Assert.Equal(new ushort[] { 0x11, 0x56, 0x56, 0x11 }, paste.Cast<object>().Select(KeyCode));
        Assert.Equal(new uint[] { 0, 0, 2, 2 }, paste.Cast<object>().Select(KeyFlags));
        foreach (object key in paste) Assert.Equal(InputKeyboardHook.SyntheticInputMarker, KeyMarker(key));
        foreach (uint sent in new uint[] { 0, 1, 2, 3, 4 })
        {
            var cleanup = (Array)sender.GetMethod("CreatePasteCleanupPlan", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [sent])!;
            ushort[] expected = sent switch { 1 or 3 => [0x11], 2 => [0x56, 0x11], _ => [] };
            Assert.Equal(expected, cleanup.Cast<object>().Select(KeyCode));
            foreach (object key in cleanup)
            {
                Assert.Equal(2u, KeyFlags(key));
                Assert.Equal(InputKeyboardHook.SyntheticInputMarker, KeyMarker(key));
            }
        }
    }

    private static object Keyboard(object input)
    {
        object union = input.GetType().GetField("Data")!.GetValue(input)!;
        return union.GetType().GetField("Keyboard")!.GetValue(union)!;
    }
    private static ushort KeyCode(object input) { object key = Keyboard(input); return (ushort)key.GetType().GetField("VirtualKey")!.GetValue(key)!; }
    private static uint KeyFlags(object input) { object key = Keyboard(input); return (uint)key.GetType().GetField("Flags")!.GetValue(key)!; }
    private static nuint KeyMarker(object input) { object key = Keyboard(input); return (nuint)key.GetType().GetField("ExtraInfo")!.GetValue(key)!; }

    private sealed class Fixture
    {
        public List<string> Events { get; } = [];
        public FakeSession Session { get; }
        public FakeClipboard Clipboard { get; }
        public FakeKeyboard Keyboard { get; }
        public GenericTextInsertionBackend Backend { get; }
        public Fixture()
        {
            Session = new(Events); Clipboard = new(Events, Session); Keyboard = new(Events);
            Backend = new(Clipboard, Keyboard);
        }
    }
    private sealed class FakeSession(List<string> events) : IClipboardSession
    {
        public int RestoreCalls, DisposeCalls;
        public bool ThrowOnRestore;
        public bool Current = true;
        public string CurrentText = "translation";
        public ClipboardRestoreResult RestoreResult = new(ClipboardRestorationStatus.Restored, "restored");
        public bool IsCurrent() => Current;
        public void UserCopy(string text)
        {
            Current = false; CurrentText = text;
            RestoreResult = new(ClipboardRestorationStatus.SkippedBecauseClipboardChanged, "new clipboard preserved");
        }
        public ClipboardRestoreResult TryRestore()
        {
            RestoreCalls++; events.Add("restore");
            if (ThrowOnRestore) throw new InvalidOperationException();
            if (RestoreResult.Status == ClipboardRestorationStatus.Restored) CurrentText = "original";
            return RestoreResult;
        }
        public void Dispose() { DisposeCalls++; events.Add("dispose"); }
    }
    private sealed class FakeClipboard(List<string> events, FakeSession session) : IClipboardTransport
    {
        public bool Prepared = true, ThrowOnPrepare;
        public Action? OnPrepare;
        public int PrepareCalls, CallerThread;
        public string? PreparedText;
        public ClipboardRestorationStatus FailedRestoration;
        public ClipboardPreparationResult TryPrepare(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            PrepareCalls++; CallerThread = Environment.CurrentManagedThreadId; PreparedText = text; events.Add("prepare"); OnPrepare?.Invoke();
            if (ThrowOnPrepare) throw new InvalidOperationException();
            return Prepared ? new(true, session, "prepared") : new(false, null, "unsupported format", FailedRestoration);
        }
    }
    private sealed class FakeKeyboard(List<string> events) : ITextInputSender
    {
        public int PasteCalls, UnicodeCalls;
        public bool InputAttempted, ThrowOnPaste;
        public string? UnicodeText;
        public Action? OnPaste, BeforePasteGuard;
        public TextInputDispatchResult PasteResult = new(true, 4, 4, false, "sent");
        public TextInputDispatchResult UnicodeResult = new(true, 10, 10, false, "sent");
        public TextInputDispatchResult SendPaste(Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            PasteCalls++;
            BeforePasteGuard?.Invoke();
            if (cancellationToken.IsCancellationRequested || !stillAuthorized()) return new(false, 4, 0, false, "not authorized");
            InputAttempted = true; events.Add("paste"); OnPaste?.Invoke();
            if (ThrowOnPaste) throw new InvalidOperationException();
            return PasteResult;
        }
        public TextInputDispatchResult SendUnicode(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            UnicodeCalls++; UnicodeText = text;
            if (cancellationToken.IsCancellationRequested || !stillAuthorized()) return new(false, 10, 0, false, "not authorized");
            InputAttempted = true; events.Add("unicode");
            return UnicodeResult;
        }
    }
}
