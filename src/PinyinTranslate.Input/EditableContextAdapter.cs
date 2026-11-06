using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.UIA3;
using Interop.UIAutomationClient;

namespace PinyinTranslate.Input;

/// <summary>
/// Owns all UIA COM objects on one persistent MTA. WPF TextBox and explicitly verified
/// framework/class pairs cancel composition and verify the exact baseline before writing.
/// WPF then uses ValuePattern; verified native profiles insert Unicode.
/// Focus checks and cross-process input cannot be made atomic.
/// </summary>
public sealed class EditableContextAdapter : IDisposable
{
    public const int MaximumTextLength = 8192;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
    private readonly BlockingCollection<IWork> _queue = new();
    private readonly HashSet<TextInputProfile> _verifiedTextInputProfiles;
    private readonly bool _useLegacyWpfValuePattern;
    private readonly ITextInsertionBackend _textInsertion;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _worker;
    private int _disposed;
    private interface IWork { void Execute(UIA3Automation automation); void Fail(Exception error); }
    private sealed class Work<T>(Func<UIA3Automation, T> action, CancellationToken cancellation, TaskCompletionSource<T> completion) : IWork
    {
        public void Execute(UIA3Automation automation)
        {
            if (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); return; }
            try { completion.TrySetResult(action(automation)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        public void Fail(Exception error) => completion.TrySetException(error);
    }
    private sealed record ReadResult(EditableContextSnapshot Snapshot, IUIAutomationElement Element,
        IUIAutomationValuePattern? ValuePattern, IUIAutomationTextPattern? TextPattern);

    public EditableContextAdapter(IReadOnlyCollection<TextInputProfile>? verifiedTextInputProfiles = null,
        bool useLegacyWpfValuePattern = false, ITextInsertionBackend? textInsertionBackend = null)
    {
        _verifiedTextInputProfiles = new(verifiedTextInputProfiles ?? []);
        if (_verifiedTextInputProfiles.Any(profile => profile is null || string.IsNullOrWhiteSpace(profile.FrameworkId) || string.IsNullOrWhiteSpace(profile.ClassName)))
            throw new ArgumentException("Text input profiles require an exact framework and class.", nameof(verifiedTextInputProfiles));
        if (_verifiedTextInputProfiles.Any(profile =>
            profile.AllowWinFormsRuntimeSuffix && (profile.FrameworkId != "WinForm" || profile.ClassName is not ("WindowsForms10.Edit.app." or "WindowsForms10.RICHEDIT50W.app.")) ||
            profile.AllowRichEditValueTerminator && (profile.FrameworkId != "WinForm" || !profile.ClassName.StartsWith("WindowsForms10.RICHEDIT50W.app.", StringComparison.Ordinal))))
            throw new ArgumentException("Generated suffixes and terminal normalization are restricted to the known WinForms Edit/RichEdit profiles.", nameof(verifiedTextInputProfiles));
        _useLegacyWpfValuePattern = useLegacyWpfValuePattern;
        _textInsertion = textInsertionBackend ?? new GenericTextInsertionBackend();
        if (_textInsertion is GenericTextInsertionBackend generic) generic.WriteProgress += Progress;
        _worker = new Thread(Run) { IsBackground = true, Name = "Editable context UIA MTA" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public Task<bool> Ready => _ready.Task;
    public event Action<ImeWriteProgress>? WriteProgress;

    private void Progress(ImeWriteProgress progress)
    {
        try { WriteProgress?.Invoke(progress); } catch (Exception) { }
    }

    public Task<EditableContextSnapshot?> ReadFocusedAsync(CancellationToken cancellationToken = default) =>
        Enqueue(automation => Read(automation)?.Snapshot, cancellationToken);

    public Task<EditableContextSnapshot?> ReadPreviewFocusAsync(CancellationToken cancellationToken = default) =>
        Enqueue(automation =>
        {
            automation.ConnectionTimeout = TimeSpan.FromMilliseconds(250);
            automation.TransactionTimeout = TimeSpan.FromMilliseconds(250);
            return PreviewFocusReader.Read(new UiaPreviewFocusEnvironment(automation));
        }, cancellationToken);

    public static bool TryCreateAnchor(EditableContextSnapshot snapshot, out BaselineAnchor? anchor)
    {
        anchor = null;
        if (!snapshot.CanReplace || !snapshot.HasConsistentText ||
            !BaselineAnchor.TryCreate(snapshot.PlainText, snapshot.SelectionStartUtf16, snapshot.SelectionEndUtf16, out var parsed)) return false;
        anchor = parsed! with { Context = EditableContextIdentity.From(snapshot) };
        return true;
    }

    /// <summary>
    /// Delegates run on the MTA and must be fast and thread-safe. mayFinishCaret is
    /// independent permission to finish an already-written WPF value's reset caret
    /// after ordinary cancellation; it must reject focus, mouse and navigation intent changes.
    /// Cancellation cannot undo attempted native input.
    /// </summary>
    public Task<EditableReplaceResult> GuardedReplaceAsync(EditableContextSnapshot expected, BaselineAnchor? anchor,
        string translation, CancellationToken cancellationToken, Func<bool> stillCurrent, Func<bool>? mayFinishCaret = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        return Enqueue(automation => Replace(automation, expected, anchor, translation, cancellationToken, stillCurrent, mayFinishCaret), cancellationToken);
    }

    private EditableReplaceResult Replace(UIA3Automation automation, EditableContextSnapshot expected, BaselineAnchor? anchor,
        string translation, CancellationToken cancellation, Func<bool> stillCurrent, Func<bool>? mayFinishCaret)
    {
        bool attempted = false, written = false;
        try
        {
            if (!Current(cancellation, stillCurrent)) return new(false, false, "CancelledOrStale");
            if (!expected.CanReplace || string.IsNullOrWhiteSpace(translation) || !IsSupportedUnicodeText(translation))
                return new(false, false, "IneligibleSnapshotOrText") { UserNotice = "当前输入框或译文尚不满足自动输入条件。" };
            var identity = EditableContextIdentity.From(expected);
            var live = Read(automation, identity);
            if (live is null || !EditableContextCapabilities.HasSameTextLocation(live.Snapshot, expected, requireWritable: true))
                return new(false, false, "LiveSnapshotChanged", live?.Snapshot) { UserNotice = "按 Tab 后输入位置发生变化，已取消自动输入。" };

            if (expected.CommitMethod == EditableCommitMethod.CancelCompositionAndPaste || live.Snapshot.CommitMethod == EditableCommitMethod.CancelCompositionAndPaste ||
                expected.PreferGenericPaste || live.Snapshot.PreferGenericPaste || anchor is null || !anchor.TryMatch(live.Snapshot, out _))
            {
                // A pre-key baseline is only a hypothesis. The workflow can
                // recover its exact value from positively observed Esc effects,
                // including when the first key preceded the initial UIA read.
                return new VerifiedPasteWorkflow().Execute(expected, anchor, translation, cancellation,
                    () => Current(cancellation, stillCurrent), () => Read(automation, identity)?.Snapshot,
                    () => SendCancelComposition(cancellation, stillCurrent), _textInsertion, Progress);
            }

            string replacement = anchor.Text[..anchor.CaretUtf16] + translation + anchor.Text[anchor.CaretUtf16..];
            if (replacement.Length > MaximumTextLength) return new(false, false, "ReplacementTooLong");
            int desiredCaret = anchor.CaretUtf16 + translation.Length;

            if (expected.CommitMethod == EditableCommitMethod.CancelCompositionAndUnicode)
                return CancelAndInsert(automation, live.Snapshot, anchor, translation, replacement, desiredCaret, cancellation, stillCurrent, mayFinishCaret);
            if (expected.CommitMethod != EditableCommitMethod.ValuePattern || live.ValuePattern is null || !IsDedicatedCommitStable(expected, live.Snapshot))
                return new(false, false, "UnsupportedCommitMethod", live.Snapshot);

            // Last managed guard. No native CAS exists between this check and the COM write.
            if (!Current(cancellation, stillCurrent)) return new(false, false, "CancelledOrStale", live.Snapshot);
            attempted = true;
            live.ValuePattern.SetValue(replacement);
            written = true;
            return FinishWrittenWpfCaret(automation, identity, replacement, desiredCaret,
                cancellation, stillCurrent, mayFinishCaret);
        }
        catch (Exception error)
        {
            return new(false, written, (attempted ? "WriteAttemptedButUnverified:" : "ReadOrGuardFailed:") + error.GetType().Name,
                WriteAttempted: attempted);
        }
    }

    private CancelDispatchResult SendCancelComposition(CancellationToken cancellation, Func<bool> stillCurrent)
    {
        bool attempted = false;
        try
        {
            if (!Current(cancellation, stillCurrent) || HasHeldModifier())
                return new(false, false, false, "CancelledStaleOrModified");
            NativeInput[] escape = [KeyboardInput(0x1B, 0, 0), KeyboardInput(0x1B, 0, 2)];
            attempted = true;
            uint sent = SendInput((uint)escape.Length, escape, Marshal.SizeOf<NativeInput>());
            if (sent == 1) _ = SendInput(1, [KeyboardInput(0x1B, 0, 2)], Marshal.SizeOf<NativeInput>());
            return new(true, sent > 0, sent == escape.Length, sent == escape.Length ? "CancelSent" : "CancelRejectedOrPartial");
        }
        catch (Exception error) { return new(attempted, attempted, false, "CancelException:" + error.GetType().Name); }
    }

    private EditableReplaceResult CancelAndInsert(UIA3Automation automation, EditableContextSnapshot expected,
        BaselineAnchor anchor, string translation, string replacement, int desiredCaret,
        CancellationToken cancellation, Func<bool> stillCurrent, Func<bool>? mayFinishCaret)
    {
        bool attempted = false, written = false;
        try
        {
            // Initial profiles handle one plain-text candidate. Line breaks,
            // control characters and malformed UTF-16 need separate validation.
            if (!IsSupportedUnicodeText(translation)) return new(false, false, "UnsupportedUnicodeText");
            var identity = EditableContextIdentity.From(expected);
            if (!Current(cancellation, stillCurrent) || HasHeldModifier()) return new(false, false, "CancelledStaleOrModified");
            var beforeEscape = Read(automation, identity);
            if (beforeEscape is null || !EditableContextCapabilities.HasSameTextLocation(beforeEscape.Snapshot, expected, requireWritable: true) ||
                !anchor.TryMatch(beforeEscape.Snapshot, out _))
                return new(false, false, "BeforeEscapeSnapshotChanged", beforeEscape?.Snapshot);
            if (beforeEscape.Snapshot.PreferGenericPaste || beforeEscape.Snapshot.CommitMethod == EditableCommitMethod.CancelCompositionAndPaste)
                return new VerifiedPasteWorkflow().Execute(expected, anchor, translation, cancellation,
                    () => Current(cancellation, stillCurrent), () => Read(automation, identity)?.Snapshot,
                    () => SendCancelComposition(cancellation, stillCurrent), _textInsertion, Progress);
            if (!IsDedicatedCommitStable(expected, beforeEscape.Snapshot))
                return new(false, false, "DedicatedCommitChangedBeforeCancel", beforeEscape.Snapshot)
                { UserNotice = "输入框的写入接口已变化，本次未取消拼音或写入英文。" };
            if (!Current(cancellation, stillCurrent) || HasHeldModifier()) return new(false, false, "CancelledStaleOrModified");

            Progress(new("cancel", "CancelRequested"));
            attempted = true;
            NativeInput[] escape = [KeyboardInput(0x1B, 0, 0), KeyboardInput(0x1B, 0, 0x0002)];
            uint escapeSent = SendInput((uint)escape.Length, escape, Marshal.SizeOf<NativeInput>());
            if (escapeSent != escape.Length)
            {
                // Release only our accepted Esc-down; never resend the cancel
                // action or any text after a partially accepted native batch.
                if (escapeSent == 1) _ = SendInput(1, [KeyboardInput(0x1B, 0, 0x0002)], Marshal.SizeOf<NativeInput>());
                return new(false, false, "EscapeInputRejectedOrPartial", WriteAttempted: true);
            }

            // Esc is never followed by guessed deletion. Only the exact original
            // text and original collapsed caret permit any translation write.
            var baseline = WaitForExactText(automation, identity, anchor.Text, anchor.CaretUtf16,
                minimumMilliseconds: 200, cancellation, stillCurrent);
            Progress(new("baseline", baseline is null ? "BaselineUnavailable" : "BaselineMatched"));
            if (baseline is null) return new(false, false, "EscapeBaselineNotVerified", WriteAttempted: true);
            if (!EditableContextCapabilities.HasVerifiedCancellation(expected, baseline.Snapshot, anchor))
                return new(false, false, "CompositionEndNotVerified", baseline.Snapshot, true);
            if (!Current(cancellation, stillCurrent) || HasHeldModifier())
                return new(false, false, "CancelledStaleOrModifiedAfterEscape", baseline.Snapshot, true);
            var insertionBoundary = Read(automation, identity);
            if (insertionBoundary is null || !IsExactText(insertionBoundary.Snapshot, anchor.Text, anchor.CaretUtf16))
                return new(false, false, "AfterEscapeSnapshotChanged", insertionBoundary?.Snapshot, true);
            if (!IsDedicatedCommitStable(expected, insertionBoundary.Snapshot))
                return new(false, false, "DedicatedCommitChangedAfterCancel", insertionBoundary.Snapshot, true)
                { UserNotice = "取消拼音后输入框的写入接口发生变化，已停止本次英文输入。" };

            if (ShouldUsePostEscapeValuePattern(insertionBoundary.Snapshot))
            {
                // The tentative baseline becomes trusted only after Esc restored
                // it exactly. The isolated WPF fixture exposed VK_PACKET batch
                // corruption, so use one ValuePattern write after this guard.
                if (insertionBoundary.ValuePattern is null || insertionBoundary.TextPattern is null)
                    return new(false, false, "AfterEscapeValuePatternUnavailable", insertionBoundary.Snapshot, true);
                if (!Current(cancellation, stillCurrent) || HasHeldModifier())
                    return new(false, false, "CancelledStaleOrModifiedBeforeValue", insertionBoundary.Snapshot, true);
                Progress(new("insertion", "VerifiedWpfValue"));
                insertionBoundary.ValuePattern.SetValue(replacement);
                written = true;
                return FinishWrittenWpfCaret(automation, identity, replacement, desiredCaret,
                    cancellation, stillCurrent, mayFinishCaret);
            }

            var unicode = new NativeInput[translation.Length * 2];
            for (int index = 0; index < translation.Length; index++)
            {
                unicode[index * 2] = KeyboardInput(0, translation[index], 0x0004);
                unicode[index * 2 + 1] = KeyboardInput(0, translation[index], 0x0004 | 0x0002);
            }
            // One batch only: partial SendInput is an uncertain write, never a
            // reason to resend the translation or fall back to SetValue.
            if (!Current(cancellation, stillCurrent) || HasHeldModifier())
                return new(false, false, "CancelledStaleOrModifiedBeforeUnicode", insertionBoundary.Snapshot, true);
            Progress(new("insertion", "VerifiedNativeUnicode"));
            uint insertedEvents = SendInput((uint)unicode.Length, unicode, Marshal.SizeOf<NativeInput>());
            written = insertedEvents > 0;
            if (insertedEvents != unicode.Length)
                return new(false, written, "UnicodeInputRejectedOrPartial", WriteAttempted: true);
            Progress(new("verification", "CheckingNativeText"));
            var final = WaitForExactText(automation, identity, replacement, desiredCaret,
                minimumMilliseconds: 0, cancellation, stillCurrent);
            bool verified = final is not null && Current(cancellation, stillCurrent);
            return new(verified, true, verified ? "Replaced" : "UnicodeWrittenButUnverified", final?.Snapshot, true);
        }
        catch (Exception error)
        {
            return new(false, written, (attempted ? "InputAttemptedButUnverified:" : "InputGuardFailed:") + error.GetType().Name,
                WriteAttempted: attempted);
        }
    }

    private static bool ShouldUsePostEscapeValuePattern(EditableContextSnapshot live) =>
        !live.PreferGenericPaste && live.FrameworkId == "WPF" && live.ClassName == "TextBox" && live.HasValuePattern && live.HasTextPattern &&
        live.CanReplace && live.CommitMethod == EditableCommitMethod.CancelCompositionAndUnicode &&
        !live.IsPassword && !live.IsReadOnly && live.HasConsistentText;

    private static bool IsDedicatedCommitStable(EditableContextSnapshot expected, EditableContextSnapshot live) =>
        (expected.CommitMethod is EditableCommitMethod.ValuePattern or EditableCommitMethod.CancelCompositionAndUnicode) &&
        !live.PreferGenericPaste && live.CommitMethod == expected.CommitMethod &&
        (expected.CommitMethod != EditableCommitMethod.ValuePattern ||
            (live.HasValuePattern && live.Value == live.DocumentText && live.HasConsistentText));

    private EditableReplaceResult FinishWrittenWpfCaret(UIA3Automation automation, EditableContextIdentity identity,
        string replacement, int desiredCaret, CancellationToken cancellation, Func<bool> stillCurrent, Func<bool>? mayFinishCaret)
    {
        if (!IsCaretCompletionAuthorized(cancellation, stillCurrent, mayFinishCaret))
            return new(false, true, "ValueWrittenCaretCompletionNotAuthorized", WriteAttempted: true);
        var afterValue = Read(automation, identity);
        if (afterValue is null || afterValue.TextPattern is null || !CanFinishPostValueCaret(afterValue.Snapshot, replacement, desiredCaret))
            return new(false, true, "AfterEscapeValueTextOrCaretChanged", afterValue?.Snapshot, true);

        if (afterValue.Snapshot.SelectionStartUtf16 != desiredCaret)
        {
            // Only the known SetValue reset point (zero) may be moved. The
            // independent location-intent guard is checked again after UIA reads.
            if (!IsCaretCompletionAuthorized(cancellation, stillCurrent, mayFinishCaret))
                return new(false, true, "ValueWrittenCaretCompletionNotAuthorized", afterValue.Snapshot, true);
            RestoreCaret(afterValue.TextPattern, replacement, desiredCaret);
        }
        var finalValue = Read(automation, identity);
        bool verified = finalValue is not null && CanFinishPostValueCaret(finalValue.Snapshot, replacement, desiredCaret) &&
            finalValue.Snapshot.SelectionStartUtf16 == desiredCaret;
        bool operationCurrent = Current(cancellation, stillCurrent) && !HasHeldModifier();
        return new(verified && operationCurrent, true,
            verified ? operationCurrent ? "Replaced" : "ValueWrittenButCancelledCaretFinished" : "AfterEscapeValueWrittenButUnverified",
            finalValue?.Snapshot, true);
    }

    private bool IsCaretCompletionAuthorized(CancellationToken cancellation, Func<bool> stillCurrent, Func<bool>? mayFinishCaret)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            return mayFinishCaret is not null ? mayFinishCaret() : Current(cancellation, stillCurrent) && !HasHeldModifier();
        }
        catch (Exception) { return false; }
    }

    private static bool CanFinishPostValueCaret(EditableContextSnapshot live, string replacement, int desiredCaret) =>
        live.FrameworkId == "WPF" && live.ClassName == "TextBox" && live.HasValuePattern && live.HasTextPattern &&
        live.CanReplace && (live.CommitMethod is EditableCommitMethod.CancelCompositionAndUnicode or EditableCommitMethod.ValuePattern) &&
        !live.IsPassword && !live.IsReadOnly && live.Value == replacement && live.DocumentText == replacement &&
        live.HasSingleSelection && live.SelectionStartUtf16 == live.SelectionEndUtf16 &&
        (live.SelectionStartUtf16 == 0 || live.SelectionStartUtf16 == desiredCaret);

    private ReadResult? WaitForExactText(UIA3Automation automation, EditableContextIdentity identity,
        string expectedText, int expectedCaret, int minimumMilliseconds, CancellationToken cancellation, Func<bool> stillCurrent,
        bool checkModifiers = true)
    {
        var elapsed = Stopwatch.StartNew();
        int consecutiveMatches = 0;
        while (elapsed.ElapsedMilliseconds <= 800)
        {
            if (!Current(cancellation, stillCurrent) || (checkModifiers && HasHeldModifier())) return null;
            if (elapsed.ElapsedMilliseconds >= minimumMilliseconds)
            {
                var live = Read(automation, identity);
                if (elapsed.ElapsedMilliseconds > 800 || !Current(cancellation, stillCurrent) || (checkModifiers && HasHeldModifier())) return null;
                if (live is null) return null; // Focus/provider identity is never repaired or redirected.
                if (IsExactText(live.Snapshot, expectedText, expectedCaret))
                {
                    if (++consecutiveMatches == 2) return live;
                }
                else consecutiveMatches = 0;
            }
            Thread.Sleep(25);
        }
        return null;
    }

    private static bool IsExactText(EditableContextSnapshot live, string text, int caret) =>
        live.CanReplace && (live.CommitMethod is EditableCommitMethod.CancelCompositionAndUnicode or EditableCommitMethod.CancelCompositionAndPaste) &&
        (live.CommitMethod != EditableCommitMethod.CancelCompositionAndPaste || live.CompositionState != CompositionState.Active) &&
        !live.IsPassword && !live.IsReadOnly && live.HasTextPattern && live.HasConsistentText && live.PlainText == text &&
        live.HasSingleSelection && live.SelectionStartUtf16 == caret && live.SelectionEndUtf16 == caret;

    private static bool HasHeldModifier() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    private static bool IsSupportedUnicodeText(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsControl(character)) return false;
            if (char.IsHighSurrogate(character))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(character)) return false;
        }
        return true;
    }

    private static NativeInput KeyboardInput(ushort key, ushort scan, uint flags) => new()
    {
        Type = 1,
        Data = new NativeInputUnion { Keyboard = new NativeKeyboardInput { VirtualKey = key, ScanCode = scan, Flags = flags, ExtraInfo = InputKeyboardHook.SyntheticInputMarker } }
    };

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeMouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    private bool Current(CancellationToken cancellation, Func<bool> stillCurrent) =>
        Volatile.Read(ref _disposed) == 0 && !cancellation.IsCancellationRequested && stillCurrent() && !cancellation.IsCancellationRequested;

    private static void RestoreCaret(IUIAutomationTextPattern text, string replacement, int desiredCaret)
    {
        var document = text.DocumentRange;
        IUIAutomationTextRange caret;
        if (desiredCaret == 0)
        {
            caret = document.Clone();
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, document, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        }
        else
        {
            // UIA Character movement is not necessarily a UTF-16 count. Locate the exact prefix instead.
            string prefix = replacement[..desiredCaret];
            caret = document.FindText(prefix, 0, 0) ?? throw new InvalidOperationException("CaretPrefixUnavailable");
            if (caret.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, document,
                    TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) != 0 || caret.GetText(MaximumTextLength + 1) != prefix)
                throw new InvalidOperationException("CaretPrefixMismatch");
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, caret, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
        }
        caret.Select();
    }

    private ReadResult? Read(UIA3Automation automation, EditableContextIdentity? expected = null)
    {
        try
        {
            nint foreground = GetForegroundWindow();
            if (foreground == 0 || GetWindowThreadProcessId(foreground, out uint foregroundPid) == 0 || foregroundPid == Environment.ProcessId) return null;
            var focus = automation.NativeAutomation.GetFocusedElement();
            if (focus is null || focus.CurrentIsPassword != 0 ||
                focus.CurrentProcessId != foregroundPid || focus.CurrentProcessId == Environment.ProcessId) return null;
            int controlType = focus.CurrentControlType;
            if (controlType != UIA_ControlTypeIds.UIA_EditControlTypeId && controlType != UIA_ControlTypeIds.UIA_DocumentControlTypeId) return null;
            var snapshot = new EditableContextSnapshot
            {
                ForegroundWindow = foreground, ForegroundProcessId = (int)foregroundPid, FocusProcessId = focus.CurrentProcessId,
                FocusRuntimeId = string.Join('.', focus.GetRuntimeId() ?? Array.Empty<int>()), AutomationId = focus.CurrentAutomationId ?? "",
                ClassName = focus.CurrentClassName ?? "", FrameworkId = focus.CurrentFrameworkId ?? "", NativeWindow = focus.CurrentNativeWindowHandle,
                IsPassword = false
            };
            var verifiedTextInputProfile = _verifiedTextInputProfiles.FirstOrDefault(profile => profile.Matches(snapshot.FrameworkId, snapshot.ClassName));
            if (snapshot.FocusRuntimeId.Length == 0 || (expected is not null && EditableContextIdentity.From(snapshot) != expected)) return null;
            var text = GetPattern<IUIAutomationTextPattern>(focus, UIA_PatternIds.UIA_TextPatternId);
            var value = GetPattern<IUIAutomationValuePattern>(focus, UIA_PatternIds.UIA_ValuePatternId);
            string documentText = "";
            IUIAutomationTextRange? document = null, insertionRange = null;
            bool documentComplete = false, valueComplete = false;
            bool singleSelection = false;
            bool selectionKnownCollapsed = false, selectionConflict = false, selectionUnsupported = false;
            int selectionStart = -1, selectionEnd = -1;
            if (text is not null)
            {
                try
                {
                    document = text.DocumentRange;
                    string? content = document.GetText(MaximumTextLength + 1);
                    documentComplete = content is not null && content.Length <= MaximumTextLength;
                    if (documentComplete)
                    {
                        documentText = content!;
                        try { selectionUnsupported = (int)text.SupportedTextSelection == 0; } catch (Exception) { }
                        try
                        {
                            var selections = text.GetSelection();
                            selectionConflict = selections is not null && selections.Length > 1;
                            if (selections is not null && selections.Length == 1)
                            {
                                selectionConflict = true; // Preserve uncertainty until degeneracy is positively known.
                                var range = selections.GetElement(0);
                                selectionKnownCollapsed = range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                                    range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0;
                                selectionConflict = !selectionKnownCollapsed;
                                singleSelection = TryReadRangeOffsets(document, range, documentText, out selectionStart, out selectionEnd);
                                if (singleSelection && selectionKnownCollapsed) insertionRange = range;
                            }
                        }
                        catch (Exception) { singleSelection = false; selectionStart = selectionEnd = -1; }
                    }
                }
                catch (Exception) { singleSelection = false; selectionStart = selectionEnd = -1; }
            }

            var compositionState = ReadCompositionState(focus, documentComplete ? document : null, documentText,
                out bool hasCompositionRange, out int compositionStart, out int compositionEnd);
            bool compositionSelection = singleSelection && selectionStart < selectionEnd && hasCompositionRange &&
                selectionStart == compositionStart && selectionEnd == compositionEnd;
            bool hasActiveCaret = false;
            int activeCaret = -1;
            // A caret never erases a genuine selection. For a proven composition
            // selection it is recorded separately for the recovery policy.
            if (documentComplete && document is not null &&
                (selectionKnownCollapsed || (!selectionConflict && selectionUnsupported) || compositionSelection))
            {
                var caret = ReadActiveCaret(focus, document, documentText, out activeCaret);
                hasActiveCaret = caret is not null;
                if (caret is not null)
                {
                    insertionRange = caret;
                    if (!compositionSelection)
                    {
                        if (singleSelection && selectionStart != activeCaret)
                        {
                            // Conflicting insertion points are not repaired by
                            // choosing one provider result over the other.
                            singleSelection = false;
                            selectionStart = selectionEnd = -1;
                            insertionRange = null;
                        }
                        else
                        {
                            singleSelection = true;
                            selectionStart = selectionEnd = activeCaret;
                        }
                    }
                }
            }
            string valueText = "";
            if (value is not null && documentComplete)
            {
                try
                {
                    string? content = value.CurrentValue;
                    valueComplete = content is not null && content.Length <= MaximumTextLength;
                    if (valueComplete) valueText = content!;
                }
                catch (Exception) { valueComplete = false; }
            }

            // Keep ranges and text from the same immutable document observation.
            // A changed document remains eligible for preview, never insertion.
            if (documentComplete && document is not null)
            {
                try { documentComplete = document.GetText(MaximumTextLength + 1) == documentText; }
                catch (Exception) { documentComplete = false; }
            }
            if (!documentComplete)
            {
                singleSelection = false; selectionStart = selectionEnd = -1;
                hasActiveCaret = false; activeCaret = -1; insertionRange = null;
                hasCompositionRange = false; compositionStart = compositionEnd = -1;
            }
            bool? readOnly = ReadReadOnly(value, text, insertionRange);
            bool usableValue = value is not null && valueComplete;
            bool richDocumentText = verifiedTextInputProfile?.AllowRichEditValueTerminator == true &&
                (value is null || (valueComplete && HasCompatibleRichEditTerminator(valueText, documentText)));
            bool preferGenericPaste = documentComplete && value is not null &&
                (!usableValue || (valueText != documentText && !richDocumentText));
            snapshot = snapshot with
            {
                Value = valueText, DocumentText = documentText, HasValuePattern = usableValue, HasTextPattern = text is not null,
                HasSingleSelection = singleSelection, SelectionStartUtf16 = selectionStart, SelectionEndUtf16 = selectionEnd,
                IsReadOnly = readOnly != false, IsReadOnlyKnown = readOnly.HasValue,
                HasCompleteText = documentComplete,
                CompositionState = compositionState,
                HasActiveCompositionRange = hasCompositionRange,
                CompositionStartUtf16 = compositionStart, CompositionEndUtf16 = compositionEnd,
                HasActiveCaret = hasActiveCaret, ActiveCaretUtf16 = activeCaret,
                // Generic paste always guards the same complete TextPattern
                // document and its ranges; the optional value is never rewritten.
                UsesDocumentText = richDocumentText || !usableValue || preferGenericPaste,
                PreferGenericPaste = preferGenericPaste,
            };
            // Optional value failures/differences cannot authorize SetValue or
            // a verified specialized writer against a different representation.
            var writingProfile = preferGenericPaste ? null : verifiedTextInputProfile;
            snapshot = EditableContextCapabilities.Evaluate(snapshot, writingProfile, _useLegacyWpfValuePattern);
            var again = automation.NativeAutomation.GetFocusedElement();
            if (GetForegroundWindow() != foreground || GetWindowThreadProcessId(foreground, out uint finalPid) == 0 || finalPid != foregroundPid ||
                again is null || again.CurrentIsPassword != 0 || again.CurrentProcessId != snapshot.FocusProcessId ||
                string.Join('.', again.GetRuntimeId() ?? Array.Empty<int>()) != snapshot.FocusRuntimeId) return null;
            return new(snapshot, focus, value, text);
        }
        catch (Exception) { return null; }
    }

    private static bool HasCompatibleRichEditTerminator(string valueText, string documentText) =>
        valueText == documentText || valueText == documentText + "\r" || valueText == documentText + "\r\n" || valueText == documentText + "\0";

    private static bool? ReadReadOnly(IUIAutomationValuePattern? value, IUIAutomationTextPattern? text,
        IUIAutomationTextRange? insertionRange)
    {
        if (value is not null)
        {
            try { return value.CurrentIsReadOnly != 0; } catch (Exception) { }
        }
        bool? documentReadOnly = null;
        if (text is not null)
            try { documentReadOnly = ReadReadOnlyAttribute(text.DocumentRange); } catch (Exception) { }
        if (documentReadOnly.HasValue) return documentReadOnly;
        // Only an already validated zero-length insertion range may supplement
        // an unknown/mixed document attribute. Explicit read-only always wins.
        return insertionRange is null ? null : ReadReadOnlyAttribute(insertionRange);
    }

    private static bool? ReadReadOnlyAttribute(IUIAutomationTextRange range)
    {
        try
        {
            object attribute = range.GetAttributeValue(UIA_TextAttributeIds.UIA_IsReadOnlyAttributeId);
            // Reserved NotSupported/MixedAttribute COM objects are not writable evidence.
            return attribute is bool boolean ? boolean : attribute is int integer && integer is 0 or 1 ? integer != 0 : null;
        }
        catch (Exception) { return null; }
    }

    private static CompositionState ReadCompositionState(IUIAutomationElement focus, IUIAutomationTextRange? document,
        string documentText, out bool hasRange, out int start, out int end)
    {
        hasRange = false; start = end = -1;
        try
        {
            var edit = GetPattern<IUIAutomationTextEditPattern>(focus, UIA_PatternIds.UIA_TextEditPatternId);
            if (edit is null) return CompositionState.Unknown;
            var composition = edit.GetActiveComposition();
            if (composition is null) return CompositionState.Inactive;
            if (document is not null)
                hasRange = TryReadRangeOffsets(document, composition, documentText, out start, out end);
            return CompositionState.Active;
        }
        catch (Exception) { return CompositionState.Unknown; }
    }

    private static IUIAutomationTextRange? ReadActiveCaret(IUIAutomationElement focus, IUIAutomationTextRange document,
        string documentText, out int offset)
    {
        offset = -1;
        try
        {
            var text2 = GetPattern<IUIAutomationTextPattern2>(focus, UIA_PatternIds.UIA_TextPattern2Id);
            if (text2 is null) return null;
            var caret = text2.GetCaretRange(out var active);
            if (caret is null || active == 0 ||
                caret.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, caret,
                    TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0 ||
                !TryReadRangeOffsets(document, caret, documentText, out int start, out int end) || start != end) return null;
            offset = start;
            return caret;
        }
        catch (Exception) { return null; }
    }

    private static bool TryReadRangeOffsets(IUIAutomationTextRange document, IUIAutomationTextRange range,
        string documentText, out int start, out int end)
    {
        start = end = -1;
        try
        {
            if (range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, document,
                    TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start) < 0 ||
                range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, document,
                    TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) > 0 ||
                range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range,
                    TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) > 0) return false;
            int rangeStart = PrefixOffset(document, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, documentText);
            int rangeEnd = PrefixOffset(document, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, documentText);
            if (rangeStart < 0 || rangeEnd < rangeStart || rangeEnd > documentText.Length ||
                range.GetText(MaximumTextLength + 1) != documentText[rangeStart..rangeEnd]) return false;
            start = rangeStart; end = rangeEnd;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static int PrefixOffset(IUIAutomationTextRange document, IUIAutomationTextRange range,
        TextPatternRangeEndpoint endpoint, string documentText)
    {
        var prefix = document.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, endpoint);
        string? content = prefix.GetText(MaximumTextLength + 1);
        return content is not null && content.Length <= documentText.Length && documentText.StartsWith(content, StringComparison.Ordinal) ? content.Length : -1;
    }

    private static T? GetPattern<T>(IUIAutomationElement focus, int id) where T : class
    {
        try { return focus.GetCurrentPattern(id) as T; } catch (Exception) { return null; }
    }

    private async Task<T> Enqueue<T>(Func<UIA3Automation, T> action, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _ready.Task.WaitAsync(cancellation).ConfigureAwait(false)) throw new InvalidOperationException("UIA worker unavailable.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new Work<T>(automation =>
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return action(automation);
        }, cancellation, completion);
        try { _queue.Add(item, cancellation); }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(EditableContextAdapter)); }
        return await completion.Task.ConfigureAwait(false);
    }

    private void Run()
    {
        try
        {
            using var automation = new UIA3Automation();
            _ready.TrySetResult(true);
            foreach (var work in _queue.GetConsumingEnumerable()) work.Execute(automation);
        }
        catch (Exception error)
        {
            _ready.TrySetResult(false);
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
            while (_queue.TryTake(out var work)) work.Fail(error);
        }
        finally { _ready.TrySetResult(false); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_textInsertion is GenericTextInsertionBackend generic) generic.WriteProgress -= Progress;
        _ready.TrySetResult(false);
        _queue.CompleteAdding();
        while (_queue.TryTake(out var work)) work.Fail(new ObjectDisposedException(nameof(EditableContextAdapter)));
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromSeconds(2));
        // A blocked provider may outlive Dispose. The worker retains ownership of its COM objects.
    }
}
