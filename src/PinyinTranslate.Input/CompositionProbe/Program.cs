using System.Diagnostics;
using Interop.UIAutomationClient;

if (args.Length != 1 || !int.TryParse(args[0], out var pid) || pid <= 0) { Console.Error.WriteLine("用法：CompositionProbe <DesktopFixture PID>"); return 2; }
using var process = Process.GetProcessById(pid);
if (process.ProcessName != "DesktopFixture") { Console.Error.WriteLine("只支持 DesktopFixture 合成样例。"); return 2; }
var automation = new CUIAutomation8();
var element = automation.GetFocusedElement();
if (element == null || element.CurrentProcessId != pid || element.CurrentAutomationId != "FixtureText" || element.CurrentIsPassword != 0)
{ Console.Error.WriteLine("焦点不是合成样例文本框，拒绝读取。"); return 3; }
Console.WriteLine($"fixturePid={pid} automationId={element.CurrentAutomationId}");
try
{
    var text = element.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) as IUIAutomationTextPattern;
    if (text == null) { Console.WriteLine("TextPattern=unavailable"); return 0; }
    var document = text.DocumentRange;
    Console.WriteLine("document=" + document.GetText(-1));
    var selection = text.GetSelection();
    Console.WriteLine("selectionCount=" + selection.Length);
    for (int i = 0; i < selection.Length; i++) Describe("selection", document, selection.GetElement(i));
    try
    {
        var text2 = element.GetCurrentPattern(UIA_PatternIds.UIA_TextPattern2Id) as IUIAutomationTextPattern2;
        if (text2 == null) Console.WriteLine("TextPattern2=unavailable");
        else { var caret = text2.GetCaretRange(out var active); Console.WriteLine("caretActive=" + active); if (caret == null) Console.WriteLine("caret=null"); else Describe("caret", document, caret); }
    }
    catch (Exception e) { Console.WriteLine("TextPattern2=" + e.GetType().Name); }
    try
    {
        var edit = element.GetCurrentPattern(UIA_PatternIds.UIA_TextEditPatternId) as IUIAutomationTextEditPattern;
        if (edit == null) Console.WriteLine("TextEditPattern=unavailable");
        else { var composition = edit.GetActiveComposition(); if (composition == null) Console.WriteLine("activeComposition=null"); else Describe("activeComposition", document, composition); }
    }
    catch (Exception e) { Console.WriteLine("TextEditPattern=" + e.GetType().Name); }
}
catch (Exception e) { Console.Error.WriteLine("UIA 读取失败：" + e.GetType().Name); return 4; }
return 0;

static void Describe(string label, IUIAutomationTextRange document, IUIAutomationTextRange range)
{
    var prefix = document.Clone();
    prefix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
    var through = document.Clone();
    through.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
    Console.WriteLine($"{label}: startUtf16={prefix.GetText(-1).Length} endUtf16={through.GetText(-1).Length} text={range.GetText(-1)}");
}
