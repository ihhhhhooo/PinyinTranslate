using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlaUI.UIA3;
using Interop.UIAutomationClient;

namespace PinyinTranslate.Tools.ImeContextProbe;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);

    private sealed record Options(int Pid, string? Output, string? Label, nint? ExpectedWindow, int MaxChars);
    private sealed record Identity(nint Foreground, uint ForegroundPid, uint ForegroundThread, int FocusPid,
        string RuntimeId, string AutomationId, nint NativeWindow, string ClassName, bool IsPassword);

    [MTAThread]
    private static int Main(string[] args)
    {
        if (!Parse(args, out var options)) return 2;
        var report = new Dictionary<string, object?>
        {
            ["utc"] = DateTimeOffset.UtcNow, ["targetPid"] = options.Pid, ["label"] = options.Label,
            ["readOnly"] = true, ["testWindowAcknowledged"] = true,
            ["offsetUnit"] = "UTF-16 length of provider GetText prefixes; not an editor edit index",
            ["limitation"] = "A matching boundary snapshot does not prove uninterrupted focus, IME session ownership, or safe insertion."
        };
        int exitCode = 0;
        try
        {
            using var target = Process.GetProcessById(options.Pid);
            _ = target.Handle;
            if (target.HasExited) throw new InvalidOperationException("TargetExited");
            using var automation = new UIA3Automation();
            var native = automation.NativeAutomation;
            var focus = native.GetFocusedElement();
            if (focus is null) throw new InvalidOperationException("NoFocusedElement");
            var before = ReadIdentity(focus);
            report["before"] = FormatIdentity(before);
            if (!Allowed(before, options))
            {
                report["status"] = "rejected";
                report["reason"] = "Foreground and non-password focused element must belong to the explicit target PID; optional HWND must match.";
                exitCode = 3;
            }
            else
            {
                // Buffer all text until the foreground/focus boundary is checked again.
                var patterns = new Dictionary<string, object?>
                {
                    ["textPattern"] = Pattern<IUIAutomationTextPattern>(focus, UIA_PatternIds.UIA_TextPatternId, text =>
                    {
                        var document = text.DocumentRange;
                        var selections = text.GetSelection();
                        var ranges = new List<object?>();
                        if (selections is not null)
                            for (int i = 0; i < selections.Length; i++)
                                ranges.Add(Range(document, selections.GetElement(i), options.MaxChars));
                        return new { document = Range(document, document, options.MaxChars), selectionCount = selections?.Length ?? 0, selections = ranges };
                    }),
                    ["textPattern2"] = Pattern<IUIAutomationTextPattern2>(focus, UIA_PatternIds.UIA_TextPattern2Id, text =>
                    {
                        var caret = text.GetCaretRange(out var active);
                        return new { caretActive = active != 0, caret = Range(text.DocumentRange, caret, options.MaxChars) };
                    }),
                    ["textEditPattern"] = Pattern<IUIAutomationTextEditPattern>(focus, UIA_PatternIds.UIA_TextEditPatternId, text =>
                    {
                        var document = text.DocumentRange;
                        return new
                        {
                            activeComposition = Attempt(() => Range(document, text.GetActiveComposition(), options.MaxChars)),
                            conversionTarget = Attempt(() => Range(document, text.GetConversionTarget(), options.MaxChars))
                        };
                    })
                };
                var focusedAgain = native.GetFocusedElement();
                var after = focusedAgain is null ? null : ReadIdentity(focusedAgain);
                report["after"] = after is null ? null : FormatIdentity(after);
                if (target.HasExited || after is null || !Allowed(after, options) || before != after)
                {
                    report["status"] = "invalidated";
                    report["reason"] = "Focus/foreground changed during capture; all captured text was discarded.";
                    exitCode = 3;
                }
                else
                {
                    report["status"] = "captured";
                    report["patterns"] = patterns;
                }
            }
        }
        catch (Exception error)
        {
            report["status"] = "failed";
            report["error"] = Error(error);
            exitCode = 4;
        }
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Console.WriteLine(json);
        if (options.Output is not null)
        {
            try { File.AppendAllText(options.Output, json + Environment.NewLine, new UTF8Encoding(false)); }
            catch (Exception error)
            {
                Console.Error.WriteLine("LogWriteFailed: " + error.GetType().Name);
                return 5;
            }
        }
        return exitCode;
    }

    private static object Pattern<T>(IUIAutomationElement element, int patternId, Func<T, object?> read) where T : class
    {
        try
        {
            var pattern = element.GetCurrentPattern(patternId) as T;
            return pattern is null ? new { status = "unavailable", value = (object?)null }
                : new { status = "available", value = read(pattern) };
        }
        catch (Exception error) { return new { status = "error", error = Error(error) }; }
    }

    private static object Attempt(Func<object?> read)
    {
        try { return new { status = "read", value = read() }; }
        catch (Exception error) { return new { status = "error", error = Error(error) }; }
    }

    private static object? Range(IUIAutomationTextRange document, IUIAutomationTextRange? range, int maxChars)
    {
        if (range is null) return null;
        string text = range.GetText(maxChars + 1) ?? "";
        return new
        {
            startUtf16 = PrefixLength(document, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, maxChars),
            endUtf16 = PrefixLength(document, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, maxChars),
            sourceText = text.Length > maxChars ? text[..maxChars] : text,
            sourceTextTruncated = text.Length > maxChars,
            offsetLimit = maxChars
        };
    }

    private static int? PrefixLength(IUIAutomationTextRange document, IUIAutomationTextRange range, TextPatternRangeEndpoint endpoint, int maxChars)
    {
        var prefix = document.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, endpoint);
        int length = (prefix.GetText(maxChars + 1) ?? "").Length;
        return length > maxChars ? null : length;
    }

    private static Identity ReadIdentity(IUIAutomationElement focus)
    {
        nint foreground = GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out uint foregroundPid);
        return new(foreground, foregroundPid, thread, focus.CurrentProcessId,
            string.Join('.', focus.GetRuntimeId() ?? Array.Empty<int>()), focus.CurrentAutomationId ?? "",
            focus.CurrentNativeWindowHandle, focus.CurrentClassName ?? "", focus.CurrentIsPassword != 0);
    }

    private static bool Allowed(Identity identity, Options options) =>
        identity.Foreground != 0 && identity.ForegroundPid == (uint)options.Pid && identity.FocusPid == options.Pid &&
        !identity.IsPassword && identity.RuntimeId.Length > 0 &&
        (!options.ExpectedWindow.HasValue || identity.Foreground == options.ExpectedWindow.Value);

    private static object FormatIdentity(Identity identity)
    {
        uint nativeThread = GetWindowThreadProcessId(identity.NativeWindow, out uint nativePid);
        var nativeClass = new StringBuilder(512);
        if (identity.NativeWindow != 0) GetClassName(identity.NativeWindow, nativeClass, nativeClass.Capacity);
        return new
        {
            foregroundHwnd = Hex(identity.Foreground), foregroundPid = identity.ForegroundPid, foregroundThreadId = identity.ForegroundThread,
            focusProcessId = identity.FocusPid, focusRuntimeId = identity.RuntimeId, focusAutomationId = identity.AutomationId,
            focusNativeHwnd = Hex(identity.NativeWindow), nativeWindowPid = nativePid, nativeWindowThreadId = nativeThread,
            focusUiaClassName = identity.ClassName, nativeWindowClassName = nativeClass.ToString(),
            focusRootHwnd = Hex(identity.NativeWindow == 0 ? 0 : GetAncestor(identity.NativeWindow, 2)), isPassword = identity.IsPassword
        };
    }

    private static string Hex(nint value) => "0x" + ((nuint)value).ToString("X", CultureInfo.InvariantCulture);
    private static object Error(Exception error) => new { type = error.GetType().Name, hresult = "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) };

    private static bool Parse(string[] args, out Options options)
    {
        options = new(0, null, null, null, 8192);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool confirmed = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--test-window") { confirmed = true; continue; }
            if (args[i] is not ("--pid" or "--output" or "--label" or "--expected-hwnd" or "--max-chars") ||
                i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) return Usage();
        }
        if (!confirmed || !values.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out int pid) || pid <= 0) return Usage();
        int maxChars = 8192;
        if (values.TryGetValue("--max-chars", out var limit) && (!int.TryParse(limit, out maxChars) || maxChars is < 1 or > 32768)) return Usage();
        nint? expected = null;
        if (values.TryGetValue("--expected-hwnd", out var hwnd))
        {
            bool hex = hwnd.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(hex ? hwnd[2..] : hwnd, hex ? NumberStyles.HexNumber : NumberStyles.None,
                CultureInfo.InvariantCulture, out ulong number) || number == 0 || number > (ulong)nuint.MaxValue) return Usage();
            expected = (nint)(nuint)number;
        }
        values.TryGetValue("--output", out var output);
        if (output is not null)
        {
            // Explicit drive-rooted local files only: no UNC/device paths or mapped network drives.
            if (output.Length < 3 || !char.IsAsciiLetter(output[0]) || output[1] != ':' || (output[2] != '\\' && output[2] != '/')) return Usage();
            try
            {
                output = Path.GetFullPath(output);
                if (!string.Equals(Path.GetExtension(output), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
                    new DriveInfo(Path.GetPathRoot(output)!).DriveType != DriveType.Fixed ||
                    !Directory.Exists(Path.GetDirectoryName(output))) return Usage();
            }
            catch { return Usage(); }
        }
        values.TryGetValue("--label", out var label);
        if (label?.Length > 128) return Usage();
        options = new(pid, output, label, expected, maxChars);
        return true;
    }

    private static bool Usage()
    {
        Console.Error.WriteLine("Read-only, confirmed disposable test windows only. No keyboard, IME mutation, network or credentials.");
        Console.Error.WriteLine("ImeContextProbe --pid <PID> --test-window [--expected-hwnd <0xHEX|decimal>] [--label <phase>] [--max-chars <1..32768>] [--output <absolute-local.jsonl>]");
        Console.Error.WriteLine("Output appends one JSON object. Log directory must exist. Exit: 0 captured (interfaces may be unavailable); 2 usage; 3 rejected/invalidated; 4 probe error; 5 log write failure.");
        return false;
    }
}
