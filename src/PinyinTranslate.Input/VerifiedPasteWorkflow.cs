namespace PinyinTranslate.Input;

public sealed record CancelDispatchResult(bool InputAttempted, bool AnyInputSent, bool Complete, string Reason);
public sealed record ImeWriteProgress(string Stage, string Code);
public sealed record PasteWorkflowTiming(int SettleMilliseconds = 200, int PollMilliseconds = 25,
    int TimeoutMilliseconds = 800, int MaximumPolls = 40);

/// <summary>
/// Managed cancel/recover/insert state machine. All callbacks execute synchronously
/// on Execute's caller thread. No constructor or method directly invokes Win32/UIA.
/// stillCurrent is a scope guard, not an old-document equality or post-paste modifier check.
/// </summary>
public sealed class VerifiedPasteWorkflow
{
    private readonly PasteWorkflowTiming _timing;
    private readonly Action<int> _delay;
    private readonly Func<long> _clock;
    public VerifiedPasteWorkflow(PasteWorkflowTiming? timing = null, Action<int>? delayMilliseconds = null, Func<long>? clockMilliseconds = null)
    {
        _timing = timing ?? new();
        if (_timing.SettleMilliseconds < 0 || _timing.PollMilliseconds < 0 || _timing.TimeoutMilliseconds < _timing.SettleMilliseconds ||
            _timing.MaximumPolls is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(timing));
        _delay = delayMilliseconds ?? Thread.Sleep;
        _clock = clockMilliseconds ?? (() => Environment.TickCount64);
    }

    public EditableReplaceResult Execute(EditableContextSnapshot expectedSnapshot, BaselineAnchor? cachedAnchor, string text,
        CancellationToken cancellationToken, Func<bool> stillCurrent, Func<EditableContextSnapshot?> readSnapshot,
        Func<CancelDispatchResult> cancelComposition, ITextInsertionBackend insertion, Action<ImeWriteProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        ArgumentNullException.ThrowIfNull(stillCurrent);
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(cancelComposition);
        ArgumentNullException.ThrowIfNull(insertion);
        bool attempted = false, insertionStarted = false;
        bool Current()
        {
            try { return !cancellationToken.IsCancellationRequested && stillCurrent() && !cancellationToken.IsCancellationRequested; }
            catch (Exception) { return false; }
        }
        void Progress(string stage, string code) { try { progress?.Invoke(new(stage, code)); } catch (Exception) { } }
        EditableReplaceResult Reject(string reason, EditableContextSnapshot? snapshot = null)
        {
            Progress("rejected", reason);
            return new(false, false, reason, snapshot, attempted) { UserNotice = RejectionNotice(reason) };
        }

        try
        {
            Progress("preflight", "checking");
            if (!ValidInsertionText(text)) return Reject("InvalidInsertionText");
            if (!Current()) return Reject("CancelledOrStaleBeforeCancel");
            if (!CompositionAnchorRecovery.IsCompleteWritable(expectedSnapshot, allowCompositionSelection: true))
                return Reject("ExpectedSnapshotNotWritable");
            var before = readSnapshot();
            if (before is null || !EditableContextCapabilities.HasSameTextLocation(before, expectedSnapshot, requireWritable: true))
                return Reject("BeforeCancelSnapshotChanged", before);
            if (expectedSnapshot.CompositionState == CompositionState.Active && before.CompositionState != CompositionState.Active)
                return Reject("CompositionProbeChangedBeforeCancel", before);
            if (expectedSnapshot.CompositionState == CompositionState.Active && expectedSnapshot.HasActiveCompositionRange &&
                (!before.HasActiveCompositionRange || before.CompositionStartUtf16 != expectedSnapshot.CompositionStartUtf16 ||
                    before.CompositionEndUtf16 != expectedSnapshot.CompositionEndUtf16))
                return Reject("CompositionRangeChangedBeforeCancel", before);
            if (!Current()) return Reject("CancelledOrStaleBeforeCancel");

            Progress("cancel", "dispatching");
            attempted = true; // A throwing cancel delegate has uncertain dispatch.
            var cancelled = cancelComposition();
            attempted = cancelled.InputAttempted || cancelled.AnyInputSent;
            if (!cancelled.Complete || !cancelled.InputAttempted) return Reject("CancelDispatchIncomplete");
            attempted = true;
            Progress("baseline", "waiting");

            long baselineStart = _clock();
            if (_timing.SettleMilliseconds > 0) _delay(_timing.SettleMilliseconds);
            EditableContextSnapshot? stable = null;
            BaselineAnchor? recovered = null;
            string recoveryReason = "PostCancelSnapshotUnavailable";
            int consecutive = 0;
            for (int poll = 0; poll < _timing.MaximumPolls; poll++)
            {
                if (!Current()) return Reject("CancelledOrStaleAfterCancel");
                if (_clock() - baselineStart > _timing.TimeoutMilliseconds) break;
                var after = readSnapshot();
                if (_clock() - baselineStart > _timing.TimeoutMilliseconds) break;
                if (!Current()) return Reject("CancelledOrStaleAfterCancel");
                if (after is not null && CompositionAnchorRecovery.TryRecover(before, after, cachedAnchor, out var anchor, out recoveryReason))
                {
                    consecutive = stable is not null && EditableContextCapabilities.HasSameTextLocation(stable, after, requireWritable: true) ? consecutive + 1 : 1;
                    stable = after; recovered = anchor;
                    if (consecutive >= 2) break;
                }
                else { stable = null; recovered = null; consecutive = 0; }
                if (_timing.PollMilliseconds > 0) _delay(_timing.PollMilliseconds);
            }
            if (stable is null || recovered is null || consecutive < 2) return Reject("PostCancelUnverified:" + recoveryReason);
            Progress("baseline", recoveryReason);
            string replacement = recovered.Text[..recovered.CaretUtf16] + text + recovered.Text[recovered.CaretUtf16..];
            int desiredCaret = recovered.CaretUtf16 + text.Length;
            if (replacement.Length > EditableContextAdapter.MaximumTextLength) return Reject("ReplacementTooLong", stable);

            bool BeforeInsertion()
            {
                if (!Current()) return false;
                var boundary = readSnapshot();
                return boundary is not null && EditableContextCapabilities.HasSameTextLocation(boundary, stable, requireWritable: true) &&
                    CompositionAnchorRecovery.IsCompleteWritable(boundary) && boundary.CompositionState != CompositionState.Active &&
                    CompositionAnchorRecovery.TryRecover(before, boundary, cachedAnchor, out var check, out _) &&
                    check == recovered && Current();
            }
            if (!BeforeInsertion()) return Reject("BeforeInsertionSnapshotChanged");
            EditableContextSnapshot? verified = null;
            bool VerifyInsertion()
            {
                Progress("verification", "checking");
                long start = _clock();
                EditableContextSnapshot? previous = null;
                int matches = 0;
                for (int poll = 0; poll < _timing.MaximumPolls; poll++)
                {
                    if (!Current() || _clock() - start > _timing.TimeoutMilliseconds) return false;
                    var after = readSnapshot();
                    if (_clock() - start > _timing.TimeoutMilliseconds || !Current()) return false;
                    bool exact = after is not null && CompositionAnchorRecovery.IsCompleteWritable(after) &&
                        EditableContextIdentity.From(after) == recovered.Context && after.CompositionState != CompositionState.Active &&
                        after.PlainText == replacement && after.SelectionStartUtf16 == desiredCaret && after.SelectionEndUtf16 == desiredCaret;
                    if (exact)
                    {
                        matches = previous is not null && EditableContextCapabilities.HasSameTextLocation(previous, after!, requireWritable: true) ? matches + 1 : 1;
                        previous = after;
                        if (matches >= 2) { verified = after; return true; }
                    }
                    else { previous = null; matches = 0; }
                    if (_timing.PollMilliseconds > 0) _delay(_timing.PollMilliseconds);
                }
                return false;
            }

            Progress("insertion", "dispatching");
            insertionStarted = true;
            var result = insertion.Insert(text, BeforeInsertion, VerifyInsertion, cancellationToken);
            bool succeeded = result.InputAttempted && result.AnyInputSent && result.Verified && verified is not null && Current();
            Progress(succeeded ? "completed" : "rejected", succeeded ? "verified" : "insertion-unverified");
            return new(succeeded, result.AnyInputSent, succeeded ? "Replaced" : "InsertionUnverified:" + result.Reason,
                verified, attempted || result.InputAttempted || result.AnyInputSent)
            {
                UserNotice = result.ClipboardRestoration switch
                {
                    ClipboardRestorationStatus.RetainedForUnconfirmedPaste => "粘贴结果尚未核对，剪贴板暂时保留英文译文。",
                    ClipboardRestorationStatus.Failed => "未能恢复原剪贴板内容。",
                    _ => succeeded ? null : "未能核对粘贴结果，已停止重试，请检查当前文字。"
                }
            };
        }
        catch (Exception error)
        {
            string reason = "PasteWorkflowException:" + error.GetType().Name;
            Progress("rejected", reason);
            return new(false, insertionStarted, reason, WriteAttempted: attempted || insertionStarted)
            {
                UserNotice = insertionStarted ? "输入过程异常，结果尚未核对，请检查当前文字。" : "输入状态读取失败，本次未粘贴英文。"
            };
        }
    }

    private static bool ValidInsertionText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > EditableContextAdapter.MaximumTextLength || text.IndexOf('\0') >= 0) return false;
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private static string RejectionNotice(string reason)
    {
        if (reason.Contains("CompositionEndUnprovenWithoutTextChange", StringComparison.Ordinal))
            return "取消拼音后无法确认组合已结束，未进入剪贴板粘贴。";
        if (reason.Contains("CompositionEndProbeUnavailable", StringComparison.Ordinal))
            return "取消拼音后组合状态读取不完整，未进入剪贴板粘贴。";
        if (reason.Contains("CompositionStillActive", StringComparison.Ordinal)) return "拼音组合尚未结束，未进入剪贴板粘贴。";
        if (reason.StartsWith("CancelledOrStale", StringComparison.Ordinal)) return "输入状态已变化，本次未进入剪贴板粘贴。";
        if (reason is "BeforeCancelSnapshotChanged" or "BeforeInsertionSnapshotChanged" || reason.Contains("FocusIdentityChanged", StringComparison.Ordinal))
            return "输入位置或正文已变化，本次未粘贴英文。";
        if (reason == "ExpectedSnapshotNotWritable") return "无法确认输入框可写及光标位置，未粘贴英文。";
        if (reason == "CancelDispatchIncomplete") return "未能完整取消当前拼音，未进入剪贴板粘贴。";
        if (reason is "CompositionProbeChangedBeforeCancel" or "CompositionRangeChangedBeforeCancel")
            return "拼音组合证据已变化，未取消输入，也未进入剪贴板粘贴。";
        if (reason is "InvalidInsertionText" or "ReplacementTooLong") return "译文内容无法用于本次自动输入，未粘贴英文。";
        return "取消拼音后的正文和光标未能安全复核，本次未粘贴英文。";
    }
}
