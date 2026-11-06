using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Real workflow/recovery/backend state machines with fake reads, cancellation,
// clipboard and keyboard. No Win32 objects, UIA, clipboard, hooks or input APIs.
[Trait("Category", "HeadlessOnly")]
public sealed class VerifiedPasteWorkflowTests
{
    [Fact]
    public void ColdInlineInputRecoversAfterEscapeAndActuallyCallsThePasteBackend()
    {
        var test = new Scenario();
        var result = test.Run();
        Assert.True(result.Succeeded); Assert.True(result.TextWasWritten); Assert.True(result.WriteAttempted);
        Assert.Equal("PREFIX belong to| SUFFIX", test.Current.PlainText);
        Assert.Equal(16, test.Current.SelectionStartUtf16);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(1, test.Clipboard.Preparations);
        Assert.Equal(1, test.Keyboard.Writes); Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal(1, test.Clipboard.Session.Restores);
    }

    [Fact]
    public void IncorrectHalfTypedAnchorIsReplacedByTheObservedWholeCancellationBaseline()
    {
        var test = new Scenario();
        test.Cached = new("PREFIX shu| SUFFIX", 10, EditableContextIdentity.From(test.Before));
        var result = test.Run();
        Assert.True(result.Succeeded);
        Assert.Equal("PREFIX belong to| SUFFIX", test.Current.PlainText);
        Assert.DoesNotContain("shu", test.Current.PlainText);
        Assert.Contains(test.Progress, item => item.Code == "RecoveredAsciiCancellation");
    }

    [Fact]
    public void ExactChineseActiveCompositionSelectionCanBeCancelledWithoutDeletingOriginalText()
    {
        var before = ChineseComposition();
        var test = new Scenario(before, Snapshot("PREFIX | SUFFIX", 7, CompositionState.Inactive));
        var result = test.Run();
        Assert.True(result.Succeeded);
        Assert.Equal("PREFIX belong to| SUFFIX", test.Current.PlainText);
        Assert.Contains(test.Progress, item => item.Code == "RecoveredActiveCompositionRange");
        Assert.Equal(1, test.CancelCalls); Assert.Equal(1, test.Keyboard.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitActiveToInactiveOverlayCanCreateTheExactBaseline(bool hasZeroLengthRange)
    {
        var before = Snapshot("PREFIX | SUFFIX", 7, CompositionState.Active) with
        {
            HasActiveCompositionRange = hasZeroLengthRange, CompositionStartUtf16 = 7, CompositionEndUtf16 = 7,
            HasActiveCaret = true, ActiveCaretUtf16 = 7
        };
        var test = new Scenario(before, before with { CompositionState = CompositionState.Inactive, HasActiveCompositionRange = false });
        Assert.True(test.Run().Succeeded);
        Assert.Equal(1, test.Keyboard.PasteCalls);
    }

    [Fact]
    public void UnknownUnchangedOverlayCannotReachClipboardWithoutTrustedCachedEvidence()
    {
        var same = Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown);
        var test = new Scenario(same, same);
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.False(result.TextWasWritten); Assert.True(result.WriteAttempted);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations); Assert.Equal(0, test.Keyboard.Writes);
        Assert.Contains("CompositionEndUnprovenWithoutTextChange", result.Reason);
        Assert.Contains("取消拼音后无法确认组合已结束", result.UserNotice);
    }

    [Fact]
    public void StrictlyVerifiedCachedNativeOverlayEvidenceIsPreserved()
    {
        var same = Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown) with
        {
            FrameworkId = "WinForm", ClassName = "WindowsForms10.Edit.app.0.fake",
            CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode, AllowsOverlayComposition = true
        };
        var test = new Scenario(same, same) { Cached = new(same.PlainText, 7, EditableContextIdentity.From(same)) };
        Assert.True(test.Run().Succeeded);
        Assert.Contains(test.Progress, item => item.Code == "CachedAnchorVerified");
        Assert.Equal(1, test.Keyboard.PasteCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void ActiveProbeMustNotDisappearAfterCancellationEvenWhenAsciiTextWasRemoved()
    {
        var before = Snapshot("PREFIX shuyu| SUFFIX", 12, CompositionState.Active);
        var test = new Scenario(before, Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown));
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.Contains("CompositionEndProbeUnavailable", result.Reason);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
    }

    [Fact]
    public void ActiveProbeCannotBeDowngradedToUnknownByTheFinalPreCancelRead()
    {
        var before = Snapshot("PREFIX shuyu| SUFFIX", 12, CompositionState.Active);
        var test = new Scenario(before, Snapshot("PREFIX | SUFFIX", 7, CompositionState.Inactive));
        test.ReadOverride = (_, snapshot) => snapshot with { CompositionState = CompositionState.Unknown };
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.Equal("CompositionProbeChangedBeforeCancel", result.Reason);
        Assert.False(result.WriteAttempted); Assert.Equal(0, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
    }

    [Fact]
    public void KnownActiveRangeCannotDisappearBeforeCancelEvenWhenCaretIsCollapsed()
    {
        var before = ChineseComposition() with { SelectionStartUtf16 = 9 };
        var test = new Scenario(before, Snapshot("PREFIX | SUFFIX", 7, CompositionState.Inactive));
        test.ReadOverride = (_, snapshot) => snapshot with { HasActiveCompositionRange = false };
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.Equal("CompositionRangeChangedBeforeCancel", result.Reason);
        Assert.Equal(0, test.CancelCalls); Assert.Equal(0, test.Keyboard.Writes);
    }

    [Fact]
    public void OrdinarySelectionIsRejectedBeforeAnyCancellation()
    {
        var before = Snapshot("PREFIX shuyu| SUFFIX", 12, CompositionState.Unknown) with { SelectionStartUtf16 = 7 };
        var test = new Scenario(before, Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown));
        Assert.False(test.Run().Succeeded);
        Assert.Equal(0, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
    }

    [Fact]
    public void WrongPrefixSuffixCaretOrUserSelectionAfterCancelNeverReachInsertion()
    {
        var baseline = Snapshot("PREFIX | SUFFIX", 7, CompositionState.Inactive);
        EditableContextSnapshot[] invalid =
        [
            baseline with { Value = "prefix | SUFFIX", DocumentText = "prefix | SUFFIX" },
            baseline with { Value = "PREFIX | WRONG", DocumentText = "PREFIX | WRONG" },
            baseline with { SelectionStartUtf16 = 8, SelectionEndUtf16 = 8 },
            baseline with { SelectionEndUtf16 = 8 },
            baseline with { FocusRuntimeId = "other.fake.runtime" },
            baseline with { IsPassword = true },
            baseline with { IsReadOnly = true },
            baseline with { IsReadOnlyKnown = false }
        ];
        foreach (var after in invalid)
        {
            var test = new Scenario(ChineseComposition(), after);
            Assert.False(test.Run().Succeeded);
            Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations); Assert.Equal(0, test.Keyboard.Writes);
        }
    }

    [Fact]
    public void UnknownChineseDeletionWithoutAnActiveRangeIsNotGuessedToBePinyin()
    {
        var test = new Scenario(Snapshot("PREFIX 属于| SUFFIX", 9, CompositionState.Unknown), Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown));
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.Contains("RemovedTextIsNotAsciiPinyin", result.Reason);
        Assert.Equal(0, test.Clipboard.Preparations);
    }

    [Fact]
    public void PartialCancelIsTerminalAndDoesNotAttemptClipboardOrAnotherCancel()
    {
        var test = new Scenario { CancelResult = new(true, true, false, "partial") };
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.True(result.WriteAttempted); Assert.False(result.TextWasWritten);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
        Assert.Contains("未能完整取消", result.UserNotice);
    }

    [Fact]
    public void RejectedCancelHasNoWriteAttemptAndNoRetry()
    {
        var test = new Scenario { CancelResult = new(false, false, false, "modifier") };
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.False(result.WriteAttempted);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Keyboard.Writes);
    }

    [Fact]
    public void CancellationDuringSettlePreventsClipboardPreparation()
    {
        var test = new Scenario { Timing = new(200, 25, 800, 40) };
        test.OnDelay = () => test.Authorized = false;
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
        Assert.Contains("CancelledOrStaleAfterCancel", result.Reason);
    }

    [Fact]
    public void BaselineNeedsTwoConsistentReadsAndDoesNotCountAProviderGap()
    {
        var test = new Scenario();
        int afterReads = 0, readsAtBaseline = 0;
        test.ReadOverride = (_, snapshot) => test.CancelCalls > 0 && ++afterReads == 2 ? null : snapshot;
        test.OnProgress = item => { if (item.Stage == "baseline" && item.Code.StartsWith("Recovered", StringComparison.Ordinal)) readsAtBaseline = test.ReadCalls; };
        Assert.True(test.Run().Succeeded);
        Assert.Equal(5, readsAtBaseline); // One pre-cancel read; valid/gap/valid/valid.
    }

    [Fact]
    public void ReadWhichReturnsPastTheDeadlineCannotAuthorizeInsertion()
    {
        var test = new Scenario();
        test.ReadOverride = (_, snapshot) => { if (test.CancelCalls > 0) test.Clock += 900; return snapshot; };
        Assert.False(test.Run().Succeeded);
        Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Clipboard.Preparations);
    }

    [Fact]
    public void IrrelevantCaretMetadataJitterDoesNotDiscardExactTextAndLocationEvidence()
    {
        var test = new Scenario();
        test.ReadOverride = (count, snapshot) => snapshot with { HasActiveCaret = count % 2 == 0, ActiveCaretUtf16 = count % 2 == 0 ? snapshot.SelectionEndUtf16 : -1 };
        Assert.True(test.Run().Succeeded);
        Assert.Equal(1, test.Keyboard.Writes);
    }

    [Fact]
    public void LastAuthorizationRechecksTheVerifiedBaselineAfterClipboardPreparation()
    {
        var test = new Scenario();
        test.Clipboard.OnPrepare = () => test.Current = Snapshot("changed by user", 3, CompositionState.Inactive);
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.False(result.TextWasWritten);
        Assert.Equal(1, test.Clipboard.Preparations); Assert.Equal(0, test.Keyboard.Writes);
        Assert.Equal(1, test.Clipboard.Session.Restores); Assert.Equal(0, test.Keyboard.UnicodeCalls);
    }

    [Fact]
    public void CorruptPasteIsDetectedByTheRealWorkflowVerifierAndNeverRetried()
    {
        var test = new Scenario();
        test.Keyboard.AfterWrite = () => test.Current = Snapshot("PREFIX beloog to| SUFFIX", 16, CompositionState.Inactive);
        var result = test.Run();
        Assert.False(result.Succeeded); Assert.True(result.TextWasWritten); Assert.True(result.WriteAttempted);
        Assert.Equal(1, test.Keyboard.Writes); Assert.Equal(1, test.CancelCalls); Assert.Equal(0, test.Keyboard.UnicodeCalls);
        Assert.Equal(0, test.Clipboard.Session.Restores);
        Assert.Contains("剪贴板暂时保留英文译文", result.UserNotice);
    }

    [Fact]
    public void CorrectTextAtTheWrongCaretIsNotReportedAsSuccessful()
    {
        var test = new Scenario();
        test.Keyboard.AfterWrite = () => test.Current = test.Current with { SelectionStartUtf16 = 0, SelectionEndUtf16 = 0 };
        Assert.False(test.Run().Succeeded);
        Assert.Equal(1, test.Keyboard.Writes); Assert.Equal(0, test.Clipboard.Session.Restores);
    }

    [Fact]
    public void BackendCannotClaimSuccessWithoutCallingTheActualVerifier()
    {
        var test = new Scenario();
        var result = test.Run(new LyingBackend());
        Assert.False(result.Succeeded); Assert.True(result.WriteAttempted);
        Assert.DoesNotContain(test.Progress, item => item.Stage == "verification");
    }

    [Fact]
    public void CallbacksStayOnTheCallerAndProgressNeverContainsAnyTextOrRuntimeIdentity()
    {
        var test = new Scenario();
        int caller = Environment.CurrentManagedThreadId;
        test.CheckThread = () => Assert.Equal(caller, Environment.CurrentManagedThreadId);
        Assert.True(test.Run().Succeeded);
        Assert.Contains(test.Progress, item => item.Stage == "cancel");
        Assert.Contains(test.Progress, item => item.Stage == "verification");
        foreach (var item in test.Progress)
        {
            string serialized = item.Stage + item.Code;
            Assert.DoesNotContain(test.Before.PlainText, serialized);
            Assert.DoesNotContain(test.Translation, serialized);
            Assert.DoesNotContain(test.Before.FocusRuntimeId, serialized);
        }
    }

    private static EditableContextSnapshot Snapshot(string text, int caret, CompositionState state) => new()
    {
        ForegroundWindow = 123, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "unit.fake.runtime.42",
        FrameworkId = "Fake", ClassName = "FakeEditor", HasTextPattern = true, HasValuePattern = true,
        Value = text, DocumentText = text, HasSingleSelection = true, SelectionStartUtf16 = caret, SelectionEndUtf16 = caret,
        IsReadOnly = false, IsReadOnlyKnown = true, HasCompleteText = true, CanPreview = true, CanReplace = true,
        CommitMethod = EditableCommitMethod.CancelCompositionAndPaste, CompositionState = state
    };
    private static EditableContextSnapshot ChineseComposition() => Snapshot("PREFIX 属于| SUFFIX", 9, CompositionState.Active) with
    {
        HasActiveCompositionRange = true, CompositionStartUtf16 = 7, CompositionEndUtf16 = 9,
        HasActiveCaret = true, ActiveCaretUtf16 = 9, SelectionStartUtf16 = 7, SelectionEndUtf16 = 9
    };

    private sealed class Scenario
    {
        public EditableContextSnapshot Before, AfterCancel, Current;
        public BaselineAnchor? Cached;
        public string Translation = "belong to";
        public bool Authorized = true;
        public int CancelCalls, ReadCalls;
        public long Clock;
        public PasteWorkflowTiming Timing = new(0, 0, 800, 8);
        public CancelDispatchResult CancelResult = new(true, true, true, "sent");
        public Func<int, EditableContextSnapshot, EditableContextSnapshot?>? ReadOverride;
        public Action? OnDelay, CheckThread;
        public Action<ImeWriteProgress>? OnProgress;
        public List<ImeWriteProgress> Progress { get; } = [];
        public FakeClipboard Clipboard { get; }
        public FakeKeyboard Keyboard { get; }
        private readonly GenericTextInsertionBackend _backend;
        public Scenario() : this(Snapshot("PREFIX shuyu| SUFFIX", 12, CompositionState.Unknown), Snapshot("PREFIX | SUFFIX", 7, CompositionState.Unknown)) { }
        public Scenario(EditableContextSnapshot before, EditableContextSnapshot after)
        {
            Before = before; AfterCancel = after; Current = before;
            Clipboard = new(this); Keyboard = new(this); _backend = new(Clipboard, Keyboard);
        }
        public EditableReplaceResult Run(ITextInsertionBackend? backend = null)
        {
            var workflow = new VerifiedPasteWorkflow(Timing, milliseconds => { CheckThread?.Invoke(); Clock += milliseconds; OnDelay?.Invoke(); }, () => Clock);
            return workflow.Execute(Before, Cached, Translation, default, () => { CheckThread?.Invoke(); return Authorized; }, () =>
            {
                CheckThread?.Invoke(); ReadCalls++;
                return ReadOverride is null ? Current : ReadOverride(ReadCalls, Current);
            }, () =>
            {
                CheckThread?.Invoke(); CancelCalls++; Current = AfterCancel; return CancelResult;
            }, backend ?? _backend, item => { CheckThread?.Invoke(); Progress.Add(item); OnProgress?.Invoke(item); });
        }
    }
    private sealed class FakeSession : IClipboardSession
    {
        public int Restores;
        public bool IsCurrent() => true;
        public ClipboardRestoreResult TryRestore() { Restores++; return new(ClipboardRestorationStatus.Restored, "restored"); }
        public void Dispose() { }
    }
    private sealed class FakeClipboard(Scenario test) : IClipboardTransport
    {
        public int Preparations;
        public Action? OnPrepare;
        public FakeSession Session { get; } = new();
        public ClipboardPreparationResult TryPrepare(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            test.CheckThread?.Invoke(); Preparations++; OnPrepare?.Invoke();
            return new(true, Session, "fake prepared");
        }
    }
    private sealed class FakeKeyboard(Scenario test) : ITextInputSender
    {
        public int PasteCalls, UnicodeCalls, Writes;
        public Action? AfterWrite;
        public TextInputDispatchResult SendPaste(Func<bool> stillAuthorized, CancellationToken cancellationToken)
        {
            test.CheckThread?.Invoke(); PasteCalls++;
            if (cancellationToken.IsCancellationRequested || !stillAuthorized()) return new(false, 4, 0, false, "guard rejected");
            Writes++;
            int caret = test.AfterCancel.SelectionStartUtf16;
            string text = test.AfterCancel.PlainText[..caret] + test.Translation + test.AfterCancel.PlainText[caret..];
            test.Current = test.AfterCancel with { Value = text, DocumentText = text, SelectionStartUtf16 = caret + test.Translation.Length, SelectionEndUtf16 = caret + test.Translation.Length };
            AfterWrite?.Invoke();
            return new(true, 4, 4, false, "fake sent");
        }
        public TextInputDispatchResult SendUnicode(string text, Func<bool> stillAuthorized, CancellationToken cancellationToken)
        { UnicodeCalls++; throw new InvalidOperationException("This scenario must not fall back."); }
    }
    private sealed class LyingBackend : ITextInsertionBackend
    {
        public TextInsertionResult Insert(string text, Func<bool> stillAuthorized, Func<bool> verifyInserted, CancellationToken cancellationToken) =>
            new(true, true, true, "untrusted success", ClipboardRestorationStatus.NotUsed);
    }
}
