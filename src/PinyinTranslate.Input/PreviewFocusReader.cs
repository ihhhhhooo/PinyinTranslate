namespace PinyinTranslate.Input;

/// <summary>Only focus metadata is needed to translate and copy an IME candidate.</summary>
public interface IPreviewFocusElement
{
    int ProcessId { get; }
    string RuntimeId { get; }
    bool IsPassword { get; }
    nint NativeWindow { get; }
    string AutomationId => "";
    string ClassName => "";
    string FrameworkId => "";
    IPreviewFocusElement? Parent { get; }
}

public interface IPreviewFocusEnvironment
{
    int OwnProcessId { get; }
    nint ForegroundWindow { get; }
    int GetWindowProcessId(nint window);
    nint GetWindowRoot(nint window);
    IPreviewFocusElement? FocusedElement { get; }
}

/// <summary>
/// Read-only preview deliberately has no Edit/Document role or TextPattern gate.
/// A terminal's Text control and a Java editor's focus proxy can still own an IME.
/// No text/value/range is read, and this result can never authorize insertion.
/// </summary>
public static class PreviewFocusReader
{
    public static EditableContextSnapshot? Read(IPreviewFocusEnvironment environment)
    {
        try
        {
            nint foreground = environment.ForegroundWindow;
            int process = environment.GetWindowProcessId(foreground);
            if (foreground == 0 || process <= 0 || process == environment.OwnProcessId) return null;
            var focus = environment.FocusedElement;
            if (focus is null || focus.ProcessId <= 0 || focus.ProcessId == environment.OwnProcessId || string.IsNullOrWhiteSpace(focus.RuntimeId)) return null;
            int focusedProcess = focus.ProcessId;
            string runtime = focus.RuntimeId;
            if (!BelongsToForeground(focus, foreground, environment)) return null;
            var result = EditableContextCapabilities.Evaluate(new EditableContextSnapshot
            {
                ForegroundWindow = foreground, ForegroundProcessId = process, FocusProcessId = focusedProcess,
                FocusRuntimeId = runtime, NativeWindow = focus.NativeWindow,
                AutomationId = Optional(() => focus.AutomationId), ClassName = Optional(() => focus.ClassName),
                FrameworkId = Optional(() => focus.FrameworkId), HasCompleteText = false,
                IsReadOnlyKnown = false, IsReadOnly = true, HasVerifiedForegroundAncestry = true
            });
            var again = environment.FocusedElement;
            return environment.ForegroundWindow == foreground && environment.GetWindowProcessId(foreground) == process &&
                again is not null && again.ProcessId == focusedProcess && again.RuntimeId == runtime &&
                BelongsToForeground(again, foreground, environment) ? result : null;
        }
        catch { return null; }
    }

    private static bool BelongsToForeground(IPreviewFocusElement focus, nint foreground, IPreviewFocusEnvironment environment)
    {
        nint root = environment.GetWindowRoot(foreground);
        if (root == 0) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (IPreviewFocusElement? node = focus; node is not null && seen.Count < 32; node = node.Parent)
        {
            if (node.IsPassword || node.ProcessId <= 0 || node.ProcessId == environment.OwnProcessId ||
                string.IsNullOrWhiteSpace(node.RuntimeId) || !seen.Add(node.RuntimeId)) return false;
            nint window = node.NativeWindow;
            if (window == 0) continue;
            if (environment.GetWindowRoot(window) != root) return false;
            if (window == root) return true;
        }
        return false;
    }

    private static string Optional(Func<string> read) { try { return read() ?? ""; } catch { return ""; } }
}
