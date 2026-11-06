using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ImeStartupBindingTests
{
    [Fact]
    public async Task First_key_before_observer_ready_keeps_the_original_anchor()
    {
        var observer = new PendingObserver();
        using var fixture = new HeadlessRuntimeFixture((_, _, _) => observer);
        try
        {
            // The fixture never invokes the runtime constructor. No UIA worker,
            // global hook or native writer exists, even briefly.
            Assert.Null(fixture.Get("_keyboard"));
            Assert.Null(fixture.Get("_pointer"));
            Assert.Null(fixture.Get("_contexts"));
            var context = new EditableContextSnapshot
            {
                ForegroundWindow = 1, ForegroundProcessId = Environment.ProcessId, FocusProcessId = Environment.ProcessId,
                FocusRuntimeId = "synthetic", AutomationId = "synthetic", FrameworkId = "WPF", ClassName = "TextBox",
                Value = "prefix", DocumentText = "prefix", HasValuePattern = true, HasTextPattern = true,
                HasSingleSelection = true, SelectionStartUtf16 = 6, SelectionEndUtf16 = 6,
                IsReadOnly = false, CanPreview = true, CanReplace = true, CommitMethod = EditableCommitMethod.CancelCompositionAndUnicode
            };
            fixture.Enable();
            fixture.SetContext(context);
            var bindingTask = fixture.InvokeAsync("BindAsync", context, 1L, CancellationToken.None);
            Assert.False(bindingTask.IsCompleted);
            Assert.NotNull(fixture.Get("_binding"));
            fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
            var anchor = Assert.IsType<BaselineAnchor>(fixture.Get("_anchor"));
            Assert.Equal("prefix", anchor.Text);
            Assert.Equal(6, anchor.CaretUtf16);
            observer.Startup.TrySetResult(true);
            await bindingTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(anchor, fixture.Get("_anchor"));
            Assert.True(observer.Enabled);
            Assert.Empty(fixture.Faults);
        }
        finally { observer.Startup.TrySetResult(false); }
    }

    private sealed class PendingObserver : ICandidateObserver
    {
        public TaskCompletionSource<bool> Startup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> Ready => Startup.Task;
        public bool Enabled { get; private set; }
        public bool SetEnabled(bool enabled) { Enabled = enabled; return true; }
        public bool IsCurrent(long version) => false;
        public void NotifyInputChanged() { }
        public Task<bool> ValidateAsync(CandidateObservation value, CancellationToken token = default) => Task.FromResult(false);
        public event Action<long>? Invalidated { add { } remove { } }
        public event Action? FocusInvalidated { add { } remove { } }
        public event Action<CandidateObservation>? Observed { add { } remove { } }
        public event Action<long>? CandidatesCleared { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public void Dispose() => Startup.TrySetResult(false);
    }
}
