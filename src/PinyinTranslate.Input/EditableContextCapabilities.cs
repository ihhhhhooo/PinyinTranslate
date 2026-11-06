namespace PinyinTranslate.Input;

/// <summary>Pure capability decisions. No class allowlist is needed for guarded paste.</summary>
public static class EditableContextCapabilities
{
    public static EditableContextSnapshot Evaluate(EditableContextSnapshot snapshot,
        TextInputProfile? verifiedProfile = null, bool useLegacyWpfValuePattern = false)
    {
        bool sameProcess = snapshot.ForegroundProcessId == snapshot.FocusProcessId;
        bool identity = snapshot.ForegroundWindow != 0 && snapshot.ForegroundProcessId > 0 && snapshot.FocusProcessId > 0 &&
            (sameProcess || snapshot.HasVerifiedForegroundAncestry) && !string.IsNullOrWhiteSpace(snapshot.FocusRuntimeId);
        // Read-only is a writing boundary. A valid IME candidate can still be
        // previewed when the editor/provider cannot expose writable evidence.
        bool preview = identity && !snapshot.IsPassword;
        bool complete = preview && sameProcess && snapshot.HasTextPattern && snapshot.HasCompleteText && snapshot.HasConsistentText &&
            snapshot.IsReadOnlyKnown && !snapshot.IsReadOnly && TryGetInsertionCaret(snapshot, out _);
        bool wpf = complete && !snapshot.PreferGenericPaste && snapshot.FrameworkId == "WPF" && snapshot.ClassName == "TextBox" && snapshot.HasValuePattern;
        bool verified = complete && !snapshot.PreferGenericPaste && verifiedProfile is not null && verifiedProfile.Matches(snapshot.FrameworkId, snapshot.ClassName);
        bool nativeOverlay = verified && (TextInputProfile.WinFormsEdit.Matches(snapshot.FrameworkId, snapshot.ClassName) ||
            TextInputProfile.WinFormsRichEdit.Matches(snapshot.FrameworkId, snapshot.ClassName));
        var method = !complete ? EditableCommitMethod.None : wpf && useLegacyWpfValuePattern ? EditableCommitMethod.ValuePattern :
            wpf || verified ? EditableCommitMethod.CancelCompositionAndUnicode : EditableCommitMethod.CancelCompositionAndPaste;
        string reason = !identity ? "FocusIdentityUnavailable" : snapshot.IsPassword ? "Password" :
            snapshot.IsReadOnlyKnown && snapshot.IsReadOnly ? "ReadOnly" : !snapshot.HasTextPattern ? "TextPatternMissing" :
            !snapshot.HasCompleteText ? "IncompleteOrOversizedDocument" : !snapshot.IsReadOnlyKnown ? "WritePermissionUnknown" :
            !TryGetInsertionCaret(snapshot, out _) ? "SelectionUnavailable" :
            !snapshot.HasConsistentText ? "TextRepresentationsDiffer" : method switch
            {
                EditableCommitMethod.ValuePattern => "PlainWpfValuePattern",
                EditableCommitMethod.CancelCompositionAndUnicode when wpf => "PlainWpfCancelAndUnicode",
                EditableCommitMethod.CancelCompositionAndUnicode => "VerifiedCancelAndUnicodeProfile",
                EditableCommitMethod.CancelCompositionAndPaste => "GuardedGenericPaste",
                _ => "UnsupportedOrIncompleteProfile"
            };
        return snapshot with
        {
            CanPreview = preview, CanReplace = complete, CommitMethod = method, CapabilityReason = reason,
            AllowsOverlayComposition = nativeOverlay || (method == EditableCommitMethod.CancelCompositionAndPaste &&
                snapshot.CompositionState == CompositionState.Active)
        };
    }

    public static bool TryGetInsertionCaret(EditableContextSnapshot snapshot, out int caret)
    {
        caret = -1;
        if (snapshot.HasSingleSelection && snapshot.SelectionStartUtf16 >= 0 &&
            snapshot.SelectionStartUtf16 == snapshot.SelectionEndUtf16 && snapshot.SelectionEndUtf16 <= snapshot.PlainText.Length)
        {
            caret = snapshot.SelectionStartUtf16;
            return true;
        }
        // An ordinary nonempty selection must never be reclassified as a caret.
        // Only the exact positively reported active composition selection is allowed.
        if (snapshot.HasSingleSelection && snapshot.CompositionState == CompositionState.Active && snapshot.HasActiveCompositionRange &&
            snapshot.HasActiveCaret && snapshot.CompositionStartUtf16 >= 0 && snapshot.CompositionEndUtf16 >= snapshot.CompositionStartUtf16 &&
            snapshot.CompositionEndUtf16 <= snapshot.PlainText.Length && snapshot.SelectionStartUtf16 == snapshot.CompositionStartUtf16 &&
            snapshot.SelectionEndUtf16 == snapshot.CompositionEndUtf16 && snapshot.ActiveCaretUtf16 == snapshot.CompositionEndUtf16)
        {
            caret = snapshot.ActiveCaretUtf16;
            return true;
        }
        return false;
    }

    public static bool HasSameTextLocation(EditableContextSnapshot left, EditableContextSnapshot right, bool requireWritable = false)
    {
        if (EditableContextIdentity.From(left) != EditableContextIdentity.From(right) || left.IsPassword || right.IsPassword ||
            left.HasCompleteText != right.HasCompleteText || left.PlainText != right.PlainText ||
            left.HasSingleSelection != right.HasSingleSelection || left.SelectionStartUtf16 != right.SelectionStartUtf16 ||
            left.SelectionEndUtf16 != right.SelectionEndUtf16) return false;
        if (!requireWritable) return true;
        return left.CanReplace && right.CanReplace && left.HasConsistentText && right.HasConsistentText &&
            left.IsReadOnlyKnown && right.IsReadOnlyKnown && !left.IsReadOnly && !right.IsReadOnly &&
            TryGetInsertionCaret(left, out int leftCaret) && TryGetInsertionCaret(right, out int rightCaret) && leftCaret == rightCaret;
    }

    /// <summary>
    /// Generic overlays require positive composition-ended evidence. For inline
    /// pre-edit, exact removal of the observed pinyin back to the original caret
    /// and text is evidence; a hidden candidate window alone is never evidence.
    /// </summary>
    public static bool HasVerifiedCancellation(EditableContextSnapshot composing, EditableContextSnapshot baseline, BaselineAnchor anchor)
    {
        if (!anchor.TryMatch(composing, out string pinyin) || anchor.Context is null || EditableContextIdentity.From(composing) != anchor.Context ||
            EditableContextIdentity.From(baseline) != anchor.Context || !baseline.CanReplace || baseline.IsPassword ||
            baseline.IsReadOnly || !baseline.HasConsistentText || !baseline.HasSingleSelection ||
            baseline.PlainText != anchor.Text || baseline.SelectionStartUtf16 != anchor.CaretUtf16 ||
            baseline.SelectionEndUtf16 != anchor.CaretUtf16 || baseline.CommitMethod != composing.CommitMethod) return false;
        if (baseline.CompositionState == CompositionState.Active) return false;
        if (composing.CommitMethod != EditableCommitMethod.CancelCompositionAndPaste) return true;
        if (baseline.CompositionState == CompositionState.Inactive) return true;
        if (composing.CompositionState == CompositionState.Active) return false; // Do not lose a previously available probe.
        return pinyin.Length > 0;
    }
}
