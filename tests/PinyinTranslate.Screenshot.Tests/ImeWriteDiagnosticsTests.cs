using System.Reflection;
using System.Text.Json;
using PinyinTranslate.App;
using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ImeWriteDiagnosticsTests
{
    private static string Format(EditableContextSnapshot? context, string code = "MissingAnchor")
    {
        var type = typeof(DesktopServices).Assembly.GetType("PinyinTranslate.App.ImeWriteDiagnostics", true)!;
        return (string)type.GetMethod("FormatLine", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["tab", code, context, false, DateTimeOffset.UnixEpoch])!;
    }

    [Fact]
    public void Diagnostic_formatting_keeps_metadata_and_omits_all_text_and_identity_values()
    {
        const string privateText = "synthetic-private-source-and-translation";
        var snapshot = new EditableContextSnapshot
        {
            CanPreview = true, CanReplace = false, HasTextPattern = true, HasCompleteText = true,
            IsReadOnlyKnown = false, CapabilityReason = "WritePermissionUnknown", Value = privateText,
            DocumentText = privateText, FocusRuntimeId = "private-runtime-id", AutomationId = "private-automation-id",
            FrameworkId = "private-framework", ClassName = "private-class"
        };
        string line = Format(snapshot);
        Assert.DoesNotContain(privateText, line);
        Assert.DoesNotContain("private-runtime-id", line);
        Assert.DoesNotContain("private-automation-id", line);
        Assert.DoesNotContain("private-framework", line);
        Assert.DoesNotContain("private-class", line);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("tab", json.RootElement.GetProperty("stage").GetString());
        Assert.Equal("MissingAnchor", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(privateText.Length, json.RootElement.GetProperty("context").GetProperty("textLength").GetInt32());
    }

    [Fact]
    public void Diagnostic_formatting_is_single_line_and_does_not_construct_a_logger_or_touch_files()
    {
        string line = Format(null, "Capability\r\nUnknown");
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Capability__Unknown", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("context").ValueKind);
    }
}
