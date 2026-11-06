using System.Reflection;
using System.Runtime.CompilerServices;
using PinyinTranslate.App;

namespace PinyinTranslate.Screenshot.Tests;

// No DesktopServices constructor, dispatcher, product window or native service
// is created. Only its status publication methods and managed runtime are used.
public sealed class ImeClipboardStatusTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void A_late_copy_notice_after_mode_disabled_displays_current_status()
    {
        using var runtime = new HeadlessRuntimeFixture(copyOnly: true);
        var services = Services(runtime);
        Invoke(services, "SetStatus", "已复制译文；旧一轮成功提示");
        // Simulate delivery after mode invalidation, without making any copy.
        Invoke(services, "ImeCopyPresentationChanged");
        Assert.DoesNotContain("已复制译文", services.Status);
        Assert.Contains("已关闭", services.Status);
    }

    [Fact]
    public void A_late_copy_notice_while_waiting_for_new_candidates_cannot_display_old_success()
    {
        using var runtime = new HeadlessRuntimeFixture(copyOnly: true);
        runtime.Enable();
        var services = Services(runtime);
        Invoke(services, "SetStatus", "已复制译文；旧一轮成功提示");
        Invoke(services, "ImeCopyPresentationChanged");
        Assert.DoesNotContain("已复制译文", services.Status);
        Assert.Contains("等待微软拼音候选", services.Status);
    }

    [Fact]
    public void Explicit_bridge_or_selection_errors_take_precedence_over_dynamic_copy_progress()
    {
        using var runtime = new HeadlessRuntimeFixture(copyOnly: true);
        var services = Services(runtime);
        Invoke(services, "ImeCopyPresentationChanged");
        const string error = "候选服务连接中断（BridgeExited）；稍后重试。";
        Invoke(services, "SetStatus", error);
        Assert.Equal(error, services.Status);
    }

    private static DesktopServices Services(HeadlessRuntimeFixture runtime)
    {
        var services = (DesktopServices)RuntimeHelpers.GetUninitializedObject(typeof(DesktopServices));
        typeof(DesktopServices).GetField("_gate", PrivateInstance)!.SetValue(services, new object());
        typeof(DesktopServices).GetField("_ime", PrivateInstance)!.SetValue(services, runtime.Runtime);
        return services;
    }

    private static void Invoke(DesktopServices services, string method, params object?[] arguments) =>
        typeof(DesktopServices).GetMethod(method, PrivateInstance)!.Invoke(services, arguments);
}
