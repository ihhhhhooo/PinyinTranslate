namespace PinyinTranslate.Input;

/// <summary>Pure evidence recovery after the caller has sent exactly one composition cancel.</summary>
public static class CompositionAnchorRecovery
{
    public static bool IsCompleteWritable(EditableContextSnapshot snapshot, bool allowCompositionSelection = false)
    {
        if (snapshot.ForegroundWindow == 0 || snapshot.FocusProcessId <= 0 || snapshot.ForegroundProcessId != snapshot.FocusProcessId ||
            string.IsNullOrWhiteSpace(snapshot.FocusRuntimeId) || snapshot.IsPassword || !snapshot.IsReadOnlyKnown || snapshot.IsReadOnly ||
            !snapshot.HasTextPattern || !snapshot.HasCompleteText || !snapshot.HasConsistentText ||
            snapshot.PlainText.Length > EditableContextAdapter.MaximumTextLength || !HasValidUtf16(snapshot.PlainText)) return false;
        if (!EditableContextCapabilities.TryGetInsertionCaret(snapshot, out int caret)) return false;
        if (!allowCompositionSelection && (!snapshot.HasSingleSelection || snapshot.SelectionStartUtf16 != snapshot.SelectionEndUtf16)) return false;
        return BaselineAnchor.TryCreate(snapshot.PlainText, caret, caret, out _);
    }

    public static bool TryRecover(EditableContextSnapshot before, EditableContextSnapshot after, BaselineAnchor? cachedAnchor,
        out BaselineAnchor? anchor, out string reason)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        anchor = null;
        reason = "IncompleteOrNonWritableSnapshot";
        if (!IsCompleteWritable(before, allowCompositionSelection: true) || !IsCompleteWritable(after)) return false;
        if (EditableContextIdentity.From(before) != EditableContextIdentity.From(after)) { reason = "FocusIdentityChanged"; return false; }
        if (after.CompositionState == CompositionState.Active) { reason = "CompositionStillActive"; return false; }
        if (before.CompositionState == CompositionState.Active && after.CompositionState != CompositionState.Inactive)
        { reason = "CompositionEndProbeUnavailable"; return false; }
        _ = EditableContextCapabilities.TryGetInsertionCaret(before, out int beforeCaret);
        int afterCaret = after.SelectionStartUtf16;

        // Positive provider evidence can locate Chinese/converted pre-edit, and
        // can distinguish an actual composition selection from an ordinary selection.
        if (before.CompositionState == CompositionState.Active && before.HasActiveCompositionRange &&
            after.CompositionState == CompositionState.Inactive)
        {
            int start = before.CompositionStartUtf16, end = before.CompositionEndUtf16;
            if (start < 0 || end < start || end > before.PlainText.Length || beforeCaret != end ||
                !IsBoundary(before.PlainText, start) || !IsBoundary(before.PlainText, end))
            { reason = "InvalidActiveCompositionRange"; return false; }
            string withoutComposition = before.PlainText[..start] + before.PlainText[end..];
            if (after.PlainText != withoutComposition || afterCaret != start)
            { reason = "ActiveCompositionRemovalMismatch"; return false; }
            return Create(after, out anchor, out reason, start == end ? "RecoveredActiveOverlay" : "RecoveredActiveCompositionRange");
        }

        if (cachedAnchor is not null && cachedAnchor.Context == EditableContextIdentity.From(after) &&
            cachedAnchor.Text == after.PlainText && cachedAnchor.CaretUtf16 == afterCaret &&
            EditableContextCapabilities.HasVerifiedCancellation(before, after, cachedAnchor))
        {
            anchor = cachedAnchor;
            reason = "CachedAnchorVerified";
            return true;
        }

        if (before.PlainText == after.PlainText)
        {
            if (beforeCaret != afterCaret) { reason = "CaretChangedWithoutTextRemoval"; return false; }
            if (before.CompositionState == CompositionState.Active && after.CompositionState == CompositionState.Inactive)
                return Create(after, out anchor, out reason, "RecoveredActiveOverlay");
            reason = "CompositionEndUnprovenWithoutTextChange";
            return false;
        }

        // This describes what Esc actually removed. It never issues deletions or
        // guesses a pinyin prefix from the translated candidate.
        int removedLength = before.PlainText.Length - after.PlainText.Length;
        if (removedLength <= 0 || beforeCaret != afterCaret + removedLength)
        { reason = "CancellationDidNotRemoveExactCaretSuffix"; return false; }
        if (!before.PlainText.AsSpan(0, afterCaret).SequenceEqual(after.PlainText.AsSpan(0, afterCaret)) ||
            !before.PlainText.AsSpan(beforeCaret).SequenceEqual(after.PlainText.AsSpan(afterCaret)))
        { reason = "CancellationPrefixOrSuffixChanged"; return false; }
        var removed = before.PlainText.AsSpan(afterCaret, removedLength);
        bool hasLetter = false;
        foreach (char character in removed)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z') hasLetter = true;
            else if (character != '\'') { reason = "RemovedTextIsNotAsciiPinyin"; return false; }
        }
        if (!hasLetter) { reason = "RemovedTextIsNotAsciiPinyin"; return false; }
        return Create(after, out anchor, out reason, "RecoveredAsciiCancellation");
    }

    private static bool Create(EditableContextSnapshot snapshot, out BaselineAnchor? anchor, out string reason, string success)
    {
        if (!BaselineAnchor.TryCreate(snapshot.PlainText, snapshot.SelectionStartUtf16, snapshot.SelectionEndUtf16, out var parsed))
        { anchor = null; reason = "InvalidRecoveredCaret"; return false; }
        anchor = parsed! with { Context = EditableContextIdentity.From(snapshot) };
        reason = success;
        return true;
    }

    private static bool IsBoundary(string text, int index) => index == 0 || index == text.Length ||
        !(char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]));

    private static bool HasValidUtf16(string text)
    {
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
}
