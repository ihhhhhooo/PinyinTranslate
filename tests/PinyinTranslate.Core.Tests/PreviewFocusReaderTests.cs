using PinyinTranslate.Input;

namespace PinyinTranslate.Core.Tests;

// Focus metadata only. No adapter/native provider/window or input is created.
public sealed class PreviewFocusReaderTests
{
    [Theory]
    [InlineData("Win32", "TermControl")]
    [InlineData("Java", "SunAwtFrame")]
    [InlineData("WinUI", "CustomPane")]
    [InlineData("Chrome", "RenderWidgetHostHWND")]
    public void Non_edit_focus_can_preview_without_loading_document_text(string framework, string name)
    {
        var environment = new FakeEnvironment();
        environment.Focus.FrameworkId = framework; environment.Focus.ClassName = name;
        var result = PreviewFocusReader.Read(environment);
        Assert.NotNull(result); Assert.True(result.CanPreview); Assert.False(result.CanReplace);
        Assert.Equal(framework, result.FrameworkId); Assert.Equal(name, result.ClassName);
        Assert.False(result.HasCompleteText); Assert.False(result.HasTextPattern); Assert.False(result.HasValuePattern);
        Assert.Equal("", result.PlainText); Assert.Equal(EditableCommitMethod.None, result.CommitMethod);
        Assert.False(EditableContextAdapter.TryCreateAnchor(result, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Protected_focus_or_ancestor_cannot_preview(bool ancestor)
    {
        var environment = new FakeEnvironment();
        if (ancestor) environment.Root.IsPassword = true; else environment.Focus.IsPassword = true;
        Assert.Null(PreviewFocusReader.Read(environment));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("own")]
    [InlineData("process")]
    [InlineData("background")]
    [InlineData("unrooted")]
    [InlineData("cycle")]
    public void Unverified_focus_identity_is_rejected(string fault)
    {
        var environment = new FakeEnvironment();
        switch (fault)
        {
            case "runtime": environment.Focus.RuntimeId = ""; break;
            case "own": environment.Focus.ProcessId = 7; break;
            case "process": environment.Focus.ProcessId = 0; break;
            case "background": environment.Root.NativeWindow = 200; break;
            case "unrooted": environment.Focus.Parent = null; break;
            case "cycle": environment.Focus.Parent = environment.Focus; break;
        }
        Assert.Null(PreviewFocusReader.Read(environment));
    }

    [Fact]
    public void Focus_change_during_read_revokes_the_result()
    {
        var environment = new FakeEnvironment { ChangeFocus = true };
        Assert.Null(PreviewFocusReader.Read(environment));
    }

    [Fact]
    public void Optional_metadata_failure_does_not_hide_an_otherwise_verified_terminal_focus()
    {
        var environment = new FakeEnvironment(); environment.Focus.BrokenAutomationId = true;
        var result = PreviewFocusReader.Read(environment);
        Assert.NotNull(result); Assert.True(result.CanPreview); Assert.Equal("", result.AutomationId);
    }

    [Fact]
    public void Renderer_focus_in_another_process_can_preview_only_when_it_belongs_to_the_foreground_host()
    {
        var environment = new FakeEnvironment(); environment.Focus.ProcessId = 43;
        var result = PreviewFocusReader.Read(environment);
        Assert.NotNull(result); Assert.Equal(42, result.ForegroundProcessId); Assert.Equal(43, result.FocusProcessId);
        Assert.True(result.HasVerifiedForegroundAncestry); Assert.True(result.CanPreview); Assert.False(result.CanReplace);
        environment.Root.NativeWindow = 200;
        Assert.Null(PreviewFocusReader.Read(environment));
    }

    private sealed class Element : IPreviewFocusElement
    {
        public int ProcessId { get; set; } = 42;
        public string RuntimeId { get; set; } = "editor";
        public bool IsPassword { get; set; }
        public nint NativeWindow { get; set; }
        public bool BrokenAutomationId;
        public string AutomationId => BrokenAutomationId ? throw new InvalidOperationException() : "editor-id";
        public string ClassName { get; set; } = "TermControl";
        public string FrameworkId { get; set; } = "WinUI";
        public IPreviewFocusElement? Parent { get; set; }
    }
    private sealed class FakeEnvironment : IPreviewFocusEnvironment
    {
        public Element Root { get; } = new() { NativeWindow = 100, RuntimeId = "root" };
        public Element Focus { get; } = new();
        public bool ChangeFocus;
        private int reads;
        public FakeEnvironment() { Focus.Parent = Root; }
        public int OwnProcessId => 7;
        public nint ForegroundWindow => 100;
        public int GetWindowProcessId(nint window) => 42;
        public nint GetWindowRoot(nint window) => window;
        public IPreviewFocusElement? FocusedElement => ++reads > 1 && ChangeFocus ? Root : Focus;
    }
}
