using System.Runtime.InteropServices;
using FlaUI.UIA3;
using Interop.UIAutomationClient;

namespace PinyinTranslate.Input;

/// <summary>The app and isolated candidate observer use the same focus metadata interpretation.</summary>
internal sealed class UiaPreviewFocusEnvironment(UIA3Automation automation) : IPreviewFocusEnvironment
{
    private readonly UIA3Automation native = automation;
    public int OwnProcessId => Environment.ProcessId;
    public nint ForegroundWindow => GetForegroundWindow();
    public int GetWindowProcessId(nint window) => GetWindowThreadProcessId(window, out uint process) == 0 ? 0 : checked((int)process);
    public nint GetWindowRoot(nint window) => window == 0 ? 0 : GetAncestor(window, 2);
    public IPreviewFocusElement? FocusedElement => Wrap(native.NativeAutomation.GetFocusedElement());
    private Element? Wrap(IUIAutomationElement? element) => element is null ? null : new(this, element);
    private sealed class Element(UiaPreviewFocusEnvironment owner, IUIAutomationElement element) : IPreviewFocusElement
    {
        public int ProcessId => element.CurrentProcessId;
        public string RuntimeId => string.Join('.', element.GetRuntimeId() ?? []);
        public bool IsPassword => element.CurrentIsPassword != 0;
        public nint NativeWindow => element.CurrentNativeWindowHandle;
        public string AutomationId => element.CurrentAutomationId;
        public string ClassName => element.CurrentClassName;
        public string FrameworkId => element.CurrentFrameworkId;
        public IPreviewFocusElement? Parent => owner.Wrap(owner.native.NativeAutomation.RawViewWalker.GetParentElement(element));
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}
