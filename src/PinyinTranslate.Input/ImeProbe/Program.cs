using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

// Read-only diagnostic: do not dump arbitrary user windows or document content.
// Run only while composing a synthetic sample in the isolated test editor.
using var automation = new UIA3Automation();
if (args.Length == 1 && args[0] == "find")
{
    var matches = automation.GetDesktop().FindAllDescendants(cf => cf.ByAutomationId("IME_Candidate_Window"));
    Console.WriteLine($"candidate window matches: {matches.Length}");
    foreach (var match in matches) Dump(match, 0);
    return;
}
if (args.Length == 2 && int.TryParse(args[0], out int x) && int.TryParse(args[1], out int y))
{
    var current = automation.FromPoint(new System.Drawing.Point(x, y));
    var walker = automation.TreeWalkerFactory.GetRawViewWalker();
    for (int i = 0; current != null && i < 5; i++)
    {
        Console.WriteLine($"ANCESTOR {i}");
        var p = current.Properties;
        Console.WriteLine($"name={p.Name.ValueOrDefault} automationId={p.AutomationId.ValueOrDefault} class={p.ClassName.ValueOrDefault} type={p.ControlType.ValueOrDefault} offscreen={p.IsOffscreen.ValueOrDefault} bounds={p.BoundingRectangle.ValueOrDefault}");
        if (i == 1) Dump(current, 0);
        current = walker.GetParent(current);
    }
    return;
}
foreach (var window in automation.GetDesktop().FindAllChildren())
{
    try
    {
        var p = window.Properties;
        var className = p.ClassName.ValueOrDefault ?? "";
        var name = p.Name.ValueOrDefault ?? "";
        if (!className.Contains("Input", StringComparison.OrdinalIgnoreCase) &&
            !className.Contains("Candidate", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Microsoft Text Input", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("微软拼音", StringComparison.OrdinalIgnoreCase)) continue;
        Dump(window, 0);
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); }
}

static void Dump(AutomationElement element, int depth)
{
    if (depth > 6) return;
    try
    {
        var p = element.Properties;
        Console.WriteLine($"{new string(' ', depth * 2)}name={p.Name.ValueOrDefault} automationId={p.AutomationId.ValueOrDefault} class={p.ClassName.ValueOrDefault} type={p.ControlType.ValueOrDefault} offscreen={p.IsOffscreen.ValueOrDefault} bounds={p.BoundingRectangle.ValueOrDefault}");
        foreach (var child in element.FindAllChildren()) Dump(child, depth + 1);
    }
    catch (Exception ex) { Console.Error.WriteLine($"{new string(' ', depth * 2)}{ex.GetType().Name}"); }
}
