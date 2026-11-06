namespace PinyinTranslate.Selection;

/// <summary>Fail-closed eligibility for an externally focused UIA selection.</summary>
public static class SelectionGuard
{
    public static bool EventSourceMatches(int eventProcess, string? eventRuntimeId, int focusProcess, string? focusRuntimeId) =>
        eventProcess > 0 && eventProcess == focusProcess && !string.IsNullOrWhiteSpace(eventRuntimeId) &&
        !string.IsNullOrWhiteSpace(focusRuntimeId) && string.Equals(eventRuntimeId, focusRuntimeId, StringComparison.Ordinal);
    public static bool Allows(int ownProcess, int focusedProcess, bool protectedElementOrAncestor,
        bool ancestryComplete, nint foregroundRoot, nint targetRoot) =>
        focusedProcess > 0 && focusedProcess != ownProcess && !protectedElementOrAncestor && ancestryComplete &&
        foregroundRoot != 0 && targetRoot != 0 && foregroundRoot == targetRoot;
}
