namespace PinyinTranslate.Input;

public enum EditableCommitMethod
{
    None,
    ValuePattern,
    CancelCompositionAndUnicode,
    CancelCompositionAndPaste
}

public enum CompositionState { Unknown, Inactive, Active }

/// <summary>An explicitly verified UIA framework/class pair. Runtime suffix matching is limited to the known WinForms controls.</summary>
public sealed record TextInputProfile(string FrameworkId, string ClassName)
{
    public bool AllowWinFormsRuntimeSuffix { get; init; }
    public bool AllowRichEditValueTerminator { get; init; }
    public static TextInputProfile WinFormsEdit { get; } = new("WinForm", "WindowsForms10.Edit.app.")
        { AllowWinFormsRuntimeSuffix = true };
    public static TextInputProfile WinFormsRichEdit { get; } = new("WinForm", "WindowsForms10.RICHEDIT50W.app.")
        { AllowWinFormsRuntimeSuffix = true, AllowRichEditValueTerminator = true };

    internal bool Matches(string framework, string className) => FrameworkId == framework &&
        (AllowWinFormsRuntimeSuffix ? className.StartsWith(ClassName, StringComparison.Ordinal) : className == ClassName);
}

public sealed record EditableContextSnapshot
{
    public nint ForegroundWindow { get; init; }
    public int ForegroundProcessId { get; init; }
    public int FocusProcessId { get; init; }
    public string FocusRuntimeId { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FrameworkId { get; init; } = "";
    public nint NativeWindow { get; init; }
    public string Value { get; init; } = "";
    public string DocumentText { get; init; } = "";
    public bool HasValuePattern { get; init; }
    public bool HasTextPattern { get; init; }
    public bool HasSingleSelection { get; init; }
    public int SelectionStartUtf16 { get; init; } = -1;
    public int SelectionEndUtf16 { get; init; } = -1;
    public bool IsReadOnly { get; init; } = true;
    public bool IsReadOnlyKnown { get; init; } = true;
    public bool IsPassword { get; init; }
    public bool CanPreview { get; init; }
    /// <summary>Raw UIA ancestry was checked against the actual foreground HWND; preview only.</summary>
    public bool HasVerifiedForegroundAncestry { get; init; }
    public bool HasCompleteText { get; init; } = true;
    public CompositionState CompositionState { get; init; }
    public bool HasActiveCompositionRange { get; init; }
    public int CompositionStartUtf16 { get; init; } = -1;
    public int CompositionEndUtf16 { get; init; } = -1;
    public bool HasActiveCaret { get; init; }
    public int ActiveCaretUtf16 { get; init; } = -1;
    public bool CanReplace { get; init; }
    public EditableCommitMethod CommitMethod { get; init; }
    public bool UsesDocumentText { get; init; }
    /// <summary>Paste uses TextPattern as its sole text authority; never rewrite a conflicting ValuePattern.</summary>
    public bool PreferGenericPaste { get; init; }
    /// <summary>Verified native IME pre-edit can be an overlay excluded from the UIA document.</summary>
    public bool AllowsOverlayComposition { get; init; }
    public string CapabilityReason { get; init; } = "UnsupportedProfile";
    public string PlainText => HasTextPattern && (UsesDocumentText || !HasValuePattern) ? DocumentText : Value;
    public bool HasConsistentText => HasCompleteText && PlainText == DocumentText;
}

public sealed record EditableContextIdentity(nint ForegroundWindow, int ForegroundProcessId, int FocusProcessId,
    string FocusRuntimeId, string AutomationId, string ClassName, string FrameworkId, nint NativeWindow)
{
    public static EditableContextIdentity From(EditableContextSnapshot snapshot) => new(snapshot.ForegroundWindow,
        snapshot.ForegroundProcessId, snapshot.FocusProcessId, snapshot.FocusRuntimeId, snapshot.AutomationId,
        snapshot.ClassName, snapshot.FrameworkId, snapshot.NativeWindow);
}

/// <summary>Exact original text and collapsed UTF-16 insertion point. No guessed deletions.</summary>
public sealed record BaselineAnchor(string Text, int CaretUtf16, EditableContextIdentity? Context = null)
{
    public static bool TryCreate(string text, int selectionStartUtf16, int selectionEndUtf16, out BaselineAnchor? anchor)
    {
        anchor = null;
        if (text is null || text.Length > EditableContextAdapter.MaximumTextLength || selectionStartUtf16 != selectionEndUtf16 ||
            selectionStartUtf16 < 0 || selectionStartUtf16 > text.Length ||
            (selectionStartUtf16 > 0 && selectionStartUtf16 < text.Length &&
             char.IsHighSurrogate(text[selectionStartUtf16 - 1]) && char.IsLowSurrogate(text[selectionStartUtf16]))) return false;
        anchor = new(text, selectionStartUtf16);
        return true;
    }

    public bool TryMatch(EditableContextSnapshot live, out string composition)
    {
        composition = "";
        if (!live.CanReplace || Context is null || Context != EditableContextIdentity.From(live) || !live.HasConsistentText) return false;
        if (Text is not null && CaretUtf16 >= 0 && CaretUtf16 <= Text.Length &&
            live.CompositionState == CompositionState.Active && live.HasActiveCompositionRange &&
            live.CompositionStartUtf16 == CaretUtf16 && live.CompositionEndUtf16 >= CaretUtf16 &&
            live.CompositionEndUtf16 <= live.PlainText.Length &&
            EditableContextCapabilities.TryGetInsertionCaret(live, out int compositionCaret) && compositionCaret == live.CompositionEndUtf16)
        {
            int length = live.CompositionEndUtf16 - live.CompositionStartUtf16;
            if (live.PlainText.Length == Text.Length + length &&
                live.PlainText.AsSpan(0, CaretUtf16).SequenceEqual(Text.AsSpan(0, CaretUtf16)) &&
                live.PlainText.AsSpan(live.CompositionEndUtf16).SequenceEqual(Text.AsSpan(CaretUtf16)))
            {
                composition = live.PlainText.Substring(CaretUtf16, length);
                return true;
            }
        }
        if (live.AllowsOverlayComposition &&
            (live.CommitMethod != EditableCommitMethod.CancelCompositionAndPaste || live.CompositionState == CompositionState.Active) &&
            live.HasTextPattern && live.HasSingleSelection && !live.IsPassword && !live.IsReadOnly &&
            Text is not null && Text.Length <= EditableContextAdapter.MaximumTextLength && CaretUtf16 >= 0 && CaretUtf16 <= Text.Length &&
            live.PlainText == Text && live.SelectionStartUtf16 == CaretUtf16 && live.SelectionEndUtf16 == CaretUtf16)
            return true; // Real candidate evidence and the writer's post-Esc guard are still required.
        return TryMatch(live.PlainText, live.SelectionStartUtf16, live.SelectionEndUtf16, out composition);
    }

    public bool TryMatch(string liveText, int selectionStartUtf16, int selectionEndUtf16, out string composition)
    {
        composition = "";
        if (Text is null || liveText is null || Text.Length > EditableContextAdapter.MaximumTextLength ||
            liveText.Length > EditableContextAdapter.MaximumTextLength || CaretUtf16 < 0 || CaretUtf16 > Text.Length ||
            selectionStartUtf16 != selectionEndUtf16) return false;
        int insertedLength = liveText.Length - Text.Length;
        if (insertedLength <= 0 || selectionStartUtf16 != CaretUtf16 + insertedLength ||
            !liveText.AsSpan(0, CaretUtf16).SequenceEqual(Text.AsSpan(0, CaretUtf16)) ||
            !liveText.AsSpan(CaretUtf16 + insertedLength).SequenceEqual(Text.AsSpan(CaretUtf16))) return false;
        var inserted = liveText.AsSpan(CaretUtf16, insertedLength);
        foreach (char c in inserted)
            if (c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not '\'') return false;
        composition = inserted.ToString();
        return true;
    }
}

/// <summary>WriteAttempted may be true even if a COM failure prevented verification. Do not blindly retry.</summary>
public sealed record EditableReplaceResult(bool Succeeded, bool TextWasWritten, string Reason,
    EditableContextSnapshot? Snapshot = null, bool WriteAttempted = false)
{
    public string? UserNotice { get; init; }
}
