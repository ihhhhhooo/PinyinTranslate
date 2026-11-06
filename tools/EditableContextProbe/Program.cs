using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.UIA3;
using Interop.UIAutomationClient;
using PinyinTranslate.Input;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [MTAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out int targetPid) || targetPid <= 0 ||
            (args.Length == 2 && (!int.TryParse(args[1], out int parsedDelay) || parsedDelay < 0 || parsedDelay > 10000)))
        {
            Console.Error.WriteLine("Usage: EditableContextProbe <DesktopFixture or NativeDesktopFixture PID> [delay-ms, 0..10000]");
            return 2;
        }
        using var target = Process.GetProcessById(targetPid);
        if (target.ProcessName is not ("DesktopFixture" or "NativeDesktopFixture"))
        {
            Console.Error.WriteLine("Only the independent DesktopFixture or NativeDesktopFixture process is supported.");
            return 2;
        }
        if (args.Length == 2) await Task.Delay(int.Parse(args[1]));
        if (!IsTargetForeground(targetPid))
        {
            Console.Error.WriteLine("DesktopFixture is not foreground; no focused text was read.");
            return 3;
        }

        Console.WriteLine("adapterAssembly=" + typeof(EditableContextAdapter).Assembly.Location);
        using var adapter = new EditableContextAdapter();
        try
        {
            bool ready = await adapter.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine("Ready=" + ready);
            if (!ready || !IsTargetForeground(targetPid)) return 4;
            var snapshot = await adapter.ReadFocusedAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (snapshot is null) Console.WriteLine("Snapshot=null");
            else Write("Snapshot", new
            {
                ForegroundWindow = snapshot.ForegroundWindow.ToInt64(), snapshot.ForegroundProcessId, snapshot.FocusProcessId,
                snapshot.FocusRuntimeId, snapshot.AutomationId, snapshot.ClassName, snapshot.FrameworkId,
                NativeWindow = snapshot.NativeWindow.ToInt64(), snapshot.HasTextPattern, snapshot.HasValuePattern,
                snapshot.HasSingleSelection, snapshot.SelectionStartUtf16, snapshot.SelectionEndUtf16,
                snapshot.IsPassword, snapshot.IsReadOnly, snapshot.CanReplace, snapshot.CapabilityReason,
                snapshot.CommitMethod, snapshot.UsesDocumentText,
                ValueLength = snapshot.Value.Length, DocumentLength = snapshot.DocumentText.Length,
                ValueEqualsDocument = snapshot.Value == snapshot.DocumentText
            });
            // Expose individual COM getter failures that the adapter intentionally
            // converts to null. This never prints the text or the element name.
            if (IsTargetForeground(targetPid)) NativeDiagnostics(targetPid);
            return snapshot is null ? 5 : 0;
        }
        catch (Exception error)
        {
            ReportError("Adapter", error);
            return 6;
        }
    }

    private static bool IsTargetForeground(int targetPid)
    {
        nint window = GetForegroundWindow();
        return window != 0 && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == targetPid;
    }

    private static void NativeDiagnostics(int targetPid)
    {
        try
        {
            using var automation = new UIA3Automation();
            var focus = automation.NativeAutomation.GetFocusedElement();
            if (focus is null || focus.CurrentProcessId != targetPid || !IsTargetForeground(targetPid))
            {
                Console.WriteLine("NativeFocus=not-the-authorized-fixture");
                return;
            }
            ReportGetter("FocusProcessId", () => focus.CurrentProcessId);
            ReportGetter("ControlType", () => focus.CurrentControlType);
            ReportGetter("IsPasswordRaw", () => focus.CurrentIsPassword);
            ReportGetter("FrameworkId", () => focus.CurrentFrameworkId);
            ReportGetter("ClassName", () => focus.CurrentClassName);
            ReportGetter("AutomationId", () => focus.CurrentAutomationId);
            ReportGetter("NativeWindow", () => focus.CurrentNativeWindowHandle.ToInt64());
            ReportGetter("RuntimeId", () => string.Join('.', focus.GetRuntimeId() ?? []));
            if (focus.CurrentIsPassword != 0) return;

            IUIAutomationValuePattern? value = null;
            IUIAutomationTextPattern? text = null;
            ReportGetter("ValuePattern", () => (value = focus.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) as IUIAutomationValuePattern) is not null);
            ReportGetter("TextPattern", () => (text = focus.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) as IUIAutomationTextPattern) is not null);
            if (value is not null)
            {
                ReportGetter("IsReadOnlyRaw", () => value.CurrentIsReadOnly);
                ReportGetter("ValueLength", () => (value.CurrentValue ?? "").Length);
                ReportGetter("ValueTerminalCodeUnits", () => TerminalCodeUnits(value.CurrentValue ?? ""));
            }
            if (text is not null)
            {
                ReportGetter("DocumentLength", () => (text.DocumentRange.GetText(EditableContextAdapter.MaximumTextLength + 1) ?? "").Length);
                ReportGetter("DocumentTerminalCodeUnits", () => TerminalCodeUnits(text.DocumentRange.GetText(EditableContextAdapter.MaximumTextLength + 1) ?? ""));
                ReportGetter("SelectionCount", () => text.GetSelection().Length);
            }
        }
        catch (Exception error) { ReportError("NativeDiagnostics", error); }
    }

    private static void ReportGetter<T>(string label, Func<T> getter)
    {
        try { Write("Native." + label, getter()); }
        catch (Exception error) { ReportError("Native." + label, error); }
    }
    private static string[] TerminalCodeUnits(string text) => text.TakeLast(2).Select(character => $"U+{(int)character:X4}").ToArray();
    private static void Write<T>(string label, T value) => Console.WriteLine(label + "=" + JsonSerializer.Serialize(value));
    private static void ReportError(string label, Exception error) =>
        Console.WriteLine($"{label}=ERROR {error.GetType().Name} HRESULT=0x{error.HResult:X8}");
}
