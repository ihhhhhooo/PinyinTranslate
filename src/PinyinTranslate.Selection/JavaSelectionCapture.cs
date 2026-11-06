namespace PinyinTranslate.Selection;

public readonly record struct JavaContext(int VmId, long ObjectId);
public sealed record JavaContextInfo(string Role, string States, bool HasText, SelectionBounds Bounds)
{
    public bool IsProtected => Role.Equals("password text", StringComparison.OrdinalIgnoreCase);
    public bool IsShowing => States.Split(',').Select(s => s.Trim()).Contains("showing", StringComparer.OrdinalIgnoreCase);
}
public sealed record JavaSelectedText(long SourceVersion, int Start, int End, string Text);

/// <summary>Read-only subset of the documented Java Access Bridge API.</summary>
public interface IJavaTextAccess
{
    JavaContext? GetRoot(nint window);
    JavaContext? GetFocus(nint window);
    JavaContext? GetAt(JavaContext root, int x, int y);
    JavaContext? GetParent(JavaContext context);
    JavaContextInfo? GetInfo(JavaContext context);
    (int Start, int End)? GetSelection(JavaContext context);
    string? GetText(JavaContext context, int start, int end);
    bool IsSame(JavaContext left, JavaContext right);
    void Release(JavaContext context);
}

/// <summary>
/// One worker owns the retained Java reference. Never read the whole document or
/// use its name/value in place of a selection. Object identity and both offsets
/// remain part of validation, including an unchanged string in another editor.
/// </summary>
public sealed class JavaSelectionCapture(IJavaTextAccess api) : IDisposable
{
    public const int MaximumTextLength = 4000;
    private JavaContext? retained;
    private long sourceVersion;
    public string? UnavailableReason { get; private set; }

    public JavaSelectedText? Read(nint window, int? x, int? y, Func<bool> allowed)
    {
        UnavailableReason = null;
        var owned = new List<JavaContext>();
        JavaContext? Own(JavaContext? value) { if (value.HasValue) owned.Add(value.Value); return value; }
        try
        {
            if (!allowed()) return null;
            var root = Own(api.GetRoot(window));
            if (root is null)
            {
                UnavailableReason = "IDEA/Java 编辑区未开放选区接口。请开启 IDE 的“支持屏幕阅读器”和 Java Access Bridge 后重启 IDE；也可使用截图翻译。";
                return null;
            }
            var node = Own(x.HasValue && y.HasValue ? api.GetAt(root.Value, x.Value, y.Value) : api.GetFocus(window));
            JavaContext? source = null;
            bool reachedRoot = false;
            for (int depth = 0; node.HasValue && depth < 32 && allowed(); depth++)
            {
                if (node.Value.VmId != root.Value.VmId) return null;
                var info = api.GetInfo(node.Value);
                if (info is null || info.IsProtected) return null;
                if (source is null && info.HasText)
                {
                    if (!info.IsShowing || (x.HasValue && y.HasValue && !info.Bounds.Contains(x.Value, y.Value))) return null;
                    source = node;
                }
                if (api.IsSame(node.Value, root.Value)) { reachedRoot = true; break; }
                node = Own(api.GetParent(node.Value));
            }
            if (!reachedRoot || source is null || !allowed()) return null;
            var before = Capture(source.Value, allowed);
            var after = before is null ? null : Capture(source.Value, allowed);
            if (before is null || after != before || !allowed()) return null;
            if (retained is null || !api.IsSame(retained.Value, source.Value))
            {
                Clear(); retained = source; sourceVersion++;
                // Transfer exactly one acquired reference to the capture owner.
                owned.Remove(source.Value);
            }
            return new(sourceVersion, before.Value.Start, before.Value.End, before.Value.Text);
        }
        catch { return null; }
        finally
        {
            foreach (var context in owned) try { api.Release(context); } catch { }
        }
    }

    private (int Start, int End, string Text)? Capture(JavaContext context, Func<bool> allowed)
    {
        if (!allowed()) return null;
        var info = api.GetInfo(context);
        if (info is null || info.IsProtected || !info.HasText || !info.IsShowing) return null;
        var range = api.GetSelection(context);
        if (range is null || range.Value.Start < 0 || range.Value.End <= range.Value.Start) return null;
        long length = (long)range.Value.End - range.Value.Start;
        if (length > MaximumTextLength)
        {
            UnavailableReason = "选中文字超过 4000 字，请缩短选区后重试。";
            return null;
        }
        if (!allowed()) return null;
        string? text = api.GetText(context, range.Value.Start, range.Value.End);
        var again = api.GetSelection(context);
        return allowed() && again == range && text is not null && text.Length == length &&
            !string.IsNullOrWhiteSpace(text) && ValidText(text) ? (range.Value.Start, range.Value.End, text) : null;
    }

    private static bool ValidText(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\0' || char.IsLowSurrogate(text[i])) return false;
            else if (char.IsHighSurrogate(text[i]) && (++i >= text.Length || !char.IsLowSurrogate(text[i]))) return false;
        return true;
    }
    private void Clear() { if (retained is { } context) { retained = null; try { api.Release(context); } catch { } } }
    public void Dispose() => Clear();
}
