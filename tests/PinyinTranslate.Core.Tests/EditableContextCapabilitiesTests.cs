using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Synthetic snapshots only. These tests never construct an adapter/UIA worker,
// open the clipboard, install hooks, create a window or send keyboard input.
public sealed class EditableContextCapabilitiesTests
{
    private static EditableContextSnapshot Plain() => new()
    {
        ForegroundWindow = 1, ForegroundProcessId = 42, FocusProcessId = 42, FocusRuntimeId = "synthetic-editor",
        FrameworkId = "UnlistedFramework", ClassName = "UnlistedEditor", HasTextPattern = true, HasValuePattern = true,
        HasCompleteText = true, IsReadOnly = false, IsReadOnlyKnown = true, Value = "PREFIX | SUFFIX", DocumentText = "PREFIX | SUFFIX",
        HasSingleSelection = true, SelectionStartUtf16 = 7, SelectionEndUtf16 = 7
    };

    [Fact]
    public void Proven_cross_process_host_allows_preview_but_never_authorizes_a_writer()
    {
        var raw = Plain() with { ForegroundProcessId = 43, HasVerifiedForegroundAncestry = true };
        var result = EditableContextCapabilities.Evaluate(raw);
        Assert.True(result.CanPreview); Assert.False(result.CanReplace); Assert.Equal(EditableCommitMethod.None, result.CommitMethod);
        Assert.False(EditableContextCapabilities.Evaluate(raw with { HasVerifiedForegroundAncestry = false }).CanPreview);
    }

    [Theory]
    [InlineData("Chrome", "SyntheticInput")]
    [InlineData("Qt", "SyntheticEdit")]
    [InlineData("WinUI", "SyntheticTextBox")]
    [InlineData("Win32", "Edit")]
    public void Complete_writable_snapshots_use_paste_without_a_framework_allowlist(string framework, string className)
    {
        var result = EditableContextCapabilities.Evaluate(Plain() with { FrameworkId = framework, ClassName = className });
        Assert.True(result.CanPreview);
        Assert.True(result.CanReplace);
        Assert.Equal(EditableCommitMethod.CancelCompositionAndPaste, result.CommitMethod);
        Assert.False(result.AllowsOverlayComposition);
    }

    [Fact]
    public void Missing_text_interface_does_not_suppress_candidate_preview()
    {
        var result = EditableContextCapabilities.Evaluate(Plain() with { HasTextPattern = false, HasCompleteText = false });
        Assert.True(result.CanPreview);
        Assert.False(result.CanReplace);
        Assert.Equal(EditableCommitMethod.None, result.CommitMethod);
        Assert.False(EditableContextAdapter.TryCreateAnchor(result, out _));
    }

    [Fact]
    public void Incomplete_document_keeps_preview_but_cannot_be_used_as_an_empty_baseline()
    {
        var result = EditableContextCapabilities.Evaluate(Plain() with
        {
            HasCompleteText = false, Value = "", DocumentText = "", SelectionStartUtf16 = 0, SelectionEndUtf16 = 0
        });
        Assert.True(result.CanPreview);
        Assert.False(result.CanReplace);
        Assert.False(result.HasConsistentText);
        Assert.False(EditableContextAdapter.TryCreateAnchor(result, out _));
    }

    [Fact]
    public void Unknown_writability_keeps_preview_but_does_not_authorize_paste()
    {
        var result = EditableContextCapabilities.Evaluate(Plain() with { IsReadOnly = true, IsReadOnlyKnown = false });
        Assert.True(result.CanPreview);
        Assert.False(result.CanReplace);
    }

    [Fact]
    public void Password_is_hidden_while_readonly_candidate_preview_does_not_authorize_writing()
    {
        var password = EditableContextCapabilities.Evaluate(Plain() with { IsPassword = true });
        Assert.False(password.CanPreview);
        Assert.False(password.CanReplace);
        var readOnly = EditableContextCapabilities.Evaluate(Plain() with { IsReadOnly = true });
        Assert.True(readOnly.CanPreview);
        Assert.False(readOnly.CanReplace);
    }

    [Fact]
    public void Unreadable_or_different_process_focus_cannot_publish_candidates()
    {
        foreach (var sample in new[]
        {
            Plain() with { FocusRuntimeId = "" }, Plain() with { ForegroundWindow = 0 },
            Plain() with { ForegroundProcessId = 99 }, Plain() with { FocusProcessId = 0 }
        })
        {
            var result = EditableContextCapabilities.Evaluate(sample);
            Assert.False(result.CanPreview);
            Assert.False(result.CanReplace);
        }
    }

    [Fact]
    public void Ambiguous_caret_and_inconsistent_text_keep_preview_only()
    {
        foreach (var sample in new[]
        {
            Plain() with { HasSingleSelection = false }, Plain() with { SelectionEndUtf16 = 8 },
            Plain() with { SelectionStartUtf16 = -1, SelectionEndUtf16 = -1 },
            Plain() with { SelectionStartUtf16 = 500, SelectionEndUtf16 = 500 }, Plain() with { DocumentText = "different text" }
        })
        {
            var result = EditableContextCapabilities.Evaluate(sample);
            Assert.True(result.CanPreview);
            Assert.False(result.CanReplace);
        }
    }

    [Fact]
    public void Verified_paths_remain_preferred_over_generic_paste()
    {
        var wpf = Plain() with { FrameworkId = "WPF", ClassName = "TextBox" };
        Assert.Equal(EditableCommitMethod.CancelCompositionAndUnicode, EditableContextCapabilities.Evaluate(wpf).CommitMethod);
        Assert.Equal(EditableCommitMethod.ValuePattern, EditableContextCapabilities.Evaluate(wpf, useLegacyWpfValuePattern: true).CommitMethod);
        var native = Plain() with { FrameworkId = "WinForm", ClassName = "WindowsForms10.Edit.app.0.synthetic" };
        var result = EditableContextCapabilities.Evaluate(native, TextInputProfile.WinFormsEdit);
        Assert.Equal(EditableCommitMethod.CancelCompositionAndUnicode, result.CommitMethod);
        Assert.True(result.AllowsOverlayComposition);
    }

    [Fact]
    public void TextPattern_only_document_can_use_paste_without_a_ValuePattern_setter()
    {
        var result = EditableContextCapabilities.Evaluate(Plain() with { HasValuePattern = false, Value = "" });
        Assert.True(result.CanReplace);
        Assert.Equal(EditableCommitMethod.CancelCompositionAndPaste, result.CommitMethod);
        Assert.True(EditableContextAdapter.TryCreateAnchor(result, out _));
    }

    [Fact]
    public void Inline_pinyin_removal_to_the_exact_baseline_proves_cancellation()
    {
        var baseline = EditableContextCapabilities.Evaluate(Plain());
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var composing = EditableContextCapabilities.Evaluate(Plain() with
        {
            Value = "PREFIX shuyu| SUFFIX", DocumentText = "PREFIX shuyu| SUFFIX", SelectionStartUtf16 = 12, SelectionEndUtf16 = 12
        });
        Assert.True(anchor!.TryMatch(composing, out var pinyin));
        Assert.Equal("shuyu", pinyin);
        Assert.True(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { CompositionState = CompositionState.Active }, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { Value = "WRONG", DocumentText = "WRONG" }, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { FocusRuntimeId = "other" }, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { SelectionEndUtf16 = 8 }, anchor));
    }

    [Fact]
    public void Generic_overlay_requires_active_then_inactive_composition_evidence()
    {
        var baseline = EditableContextCapabilities.Evaluate(Plain() with { CompositionState = CompositionState.Inactive });
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var composing = EditableContextCapabilities.Evaluate(Plain() with { CompositionState = CompositionState.Active });
        Assert.True(anchor!.TryMatch(composing, out var preedit));
        Assert.Empty(preedit);
        Assert.True(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { CompositionState = CompositionState.Unknown }, anchor));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, composing, anchor));
    }

    [Fact]
    public void Hidden_candidate_or_unchanged_document_alone_cannot_authorize_generic_overlay()
    {
        var baseline = EditableContextCapabilities.Evaluate(Plain());
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        Assert.False(anchor!.TryMatch(baseline, out _));
        Assert.False(anchor.TryMatch(baseline with { AllowsOverlayComposition = true }, out _));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(baseline, baseline, anchor));
    }

    [Fact]
    public void Cancellation_evidence_cannot_bypass_password_or_incomplete_document_guards()
    {
        var baseline = EditableContextCapabilities.Evaluate(Plain() with { CompositionState = CompositionState.Inactive });
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var composing = EditableContextCapabilities.Evaluate(Plain() with { CompositionState = CompositionState.Active });
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { IsPassword = true }, anchor!));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { HasCompleteText = false }, anchor!));
        Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, baseline with { CommitMethod = EditableCommitMethod.None }, anchor!));
    }

    [Fact]
    public void Chinese_preedit_uses_the_exact_provider_composition_range_without_guessing_characters()
    {
        var baseline = EditableContextCapabilities.Evaluate(Plain() with { CompositionState = CompositionState.Inactive });
        Assert.True(EditableContextAdapter.TryCreateAnchor(baseline, out var anchor));
        var composing = EditableContextCapabilities.Evaluate(Plain() with
        {
            Value = "PREFIX 属于| SUFFIX", DocumentText = "PREFIX 属于| SUFFIX",
            CompositionState = CompositionState.Active, HasActiveCompositionRange = true,
            CompositionStartUtf16 = 7, CompositionEndUtf16 = 9,
            SelectionStartUtf16 = 7, SelectionEndUtf16 = 9, HasActiveCaret = true, ActiveCaretUtf16 = 9
        });
        Assert.True(composing.CanReplace);
        Assert.True(anchor!.TryMatch(composing, out string preedit));
        Assert.Equal("属于", preedit);
        Assert.False(EditableContextCapabilities.Evaluate(composing with { HasActiveCaret = false }).CanReplace);
        Assert.False(EditableContextCapabilities.Evaluate(composing with { CompositionStartUtf16 = 6 }).CanReplace);
        Assert.False(anchor.TryMatch(composing with { DocumentText = "WRONG", Value = "WRONG" }, out _));
    }

    [Fact]
    public void Optional_caret_metadata_changes_do_not_invalidate_an_unchanged_writable_location()
    {
        var snapshot = EditableContextCapabilities.Evaluate(Plain());
        var metadataChanged = snapshot with { HasActiveCaret = true, ActiveCaretUtf16 = 7, HasValuePattern = false, Value = "" };
        Assert.True(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged, requireWritable: true));
        Assert.False(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged with { DocumentText = "changed" }, true));
        Assert.False(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged with { SelectionEndUtf16 = 8 }, true));
        Assert.False(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged with { FocusRuntimeId = "other" }, true));
        Assert.False(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged with { IsReadOnly = true }, true));
        Assert.False(EditableContextCapabilities.HasSameTextLocation(snapshot, metadataChanged with { CanReplace = false }, true));
    }

    [Theory]
    [InlineData("Chrome", "Edit")]
    [InlineData("WPF", "TextBox")]
    [InlineData("WinForm", "WindowsForms10.Edit.app.0.synthetic")]
    public void Document_authority_can_paste_when_ValuePattern_differs_but_cannot_use_whole_value_rewriting(string framework, string className)
    {
        var raw = Plain() with
        {
            FrameworkId = framework, ClassName = className, Value = "different-value-projection",
            UsesDocumentText = true, PreferGenericPaste = true
        };
        var result = EditableContextCapabilities.Evaluate(raw, TextInputProfile.WinFormsEdit, useLegacyWpfValuePattern: true);
        Assert.Equal(raw.DocumentText, result.PlainText);
        Assert.True(result.CanPreview);
        Assert.True(result.CanReplace);
        Assert.Equal(EditableCommitMethod.CancelCompositionAndPaste, result.CommitMethod);
        Assert.False(result.AllowsOverlayComposition);
    }
}
