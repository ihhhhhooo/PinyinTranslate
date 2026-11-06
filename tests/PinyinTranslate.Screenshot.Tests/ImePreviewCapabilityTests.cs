using System.Collections.Concurrent;
using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ImePreviewCapabilityTests
{
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static EditableContextSnapshot Context(string text = "base", bool canPreview = true, bool canReplace = true,
        EditableCommitMethod method = EditableCommitMethod.CancelCompositionAndPaste) => new()
    {
        ForegroundWindow = 1, ForegroundProcessId = Environment.ProcessId, FocusProcessId = Environment.ProcessId,
        FocusRuntimeId = "headless-editor", AutomationId = "headless-editor", FrameworkId = "Synthetic", ClassName = "Edit",
        Value = text, DocumentText = text, HasValuePattern = true, HasTextPattern = true,
        HasSingleSelection = true, SelectionStartUtf16 = text.Length, SelectionEndUtf16 = text.Length,
        IsReadOnly = false, IsReadOnlyKnown = true, HasCompleteText = true,
        CanPreview = canPreview, CanReplace = canReplace, CommitMethod = method
    };

    private static HeadlessRuntimeFixture Fixture(Observer observer, EditableContextSnapshot context)
    {
        var fixture = new HeadlessRuntimeFixture((_, _, _) => observer);
        fixture.Enable();
        fixture.SetContext(context);
        return fixture;
    }

    private static Task BindAsync(HeadlessRuntimeFixture fixture) =>
        fixture.InvokeAsync("BindAsync", fixture.Context!, 1L, CancellationToken.None);

    private static async Task ObserveAsync(HeadlessRuntimeFixture fixture, Observer observer)
    {
        var source = observer.Candidate();
        await fixture.InvokeAsync("ObserveAsync", fixture.Get("_binding")!, source);
    }

    private static bool CanAccept(HeadlessRuntimeFixture fixture) =>
        (bool)fixture.RuntimeType.GetProperty("CanAcceptCurrent")!.GetValue(fixture.Runtime)!;

    private static bool Tab(HeadlessRuntimeFixture fixture) => (bool)fixture.Invoke("TryAcceptTab", (nint)1)!;

    private static async Task<CandidateTranslationState> WaitForStateAsync(HeadlessRuntimeFixture fixture,
        Func<CandidateTranslationState, bool> predicate)
    {
        var completion = Completion<CandidateTranslationState>();
        void Changed(CandidateTranslationState state) { if (predicate(state)) completion.TrySetResult(state); }
        fixture.Coordinator.Changed += Changed;
        try
        {
            var current = fixture.Coordinator.State;
            return predicate(current) ? current : await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { fixture.Coordinator.Changed -= Changed; }
    }

    [Fact]
    public async Task Missing_text_and_anchor_do_not_block_preview_or_capture_ordinary_Tab()
    {
        var observer = new Observer();
        var context = Context(canReplace: false, method: EditableCommitMethod.None) with
        {
            HasTextPattern = false, HasValuePattern = false, HasCompleteText = false,
            HasSingleSelection = false, SelectionStartUtf16 = -1, SelectionEndUtf16 = -1,
            CapabilityReason = "TextPatternMissing"
        };
        using var fixture = Fixture(observer, context);
        int writes = 0;
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(new EditableReplaceResult(false, false, "UnexpectedWriter"));
        };
        // The fixture never runs the production constructor or creates native services.
        Assert.Null(fixture.Get("_contexts"));
        Assert.Null(fixture.Get("_keyboard"));
        Assert.Null(fixture.Get("_pointer"));
        await BindAsync(fixture);
        Assert.True(observer.Enabled);
        await ObserveAsync(fixture, observer);
        var preview = await WaitForStateAsync(fixture, x => x.Translation == "English");
        Assert.Equal("属于", preview.Candidate);
        Assert.NotNull(preview.Bounds);
        Assert.Null(fixture.Get("_anchor"));
        Assert.False(CanAccept(fixture));
        Assert.False(Tab(fixture));
        Assert.False(fixture.Coordinator.State.AcceptRequested);
        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Writable_context_without_a_captured_baseline_can_request_post_cancel_verification()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer, Context("baseshuyu", canPreview: false));
        var writerReached = Completion<bool>();
        fixture.ReplaceAsync = (_, anchor, _, _, current, _) =>
        {
            Assert.Null(anchor);
            Assert.True(current());
            writerReached.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(false, false, "HeadlessVerificationRequired"));
        };
        await BindAsync(fixture); // CanReplace also permits the preview pipeline for legacy snapshots.
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, x => x.Translation == "English");
        Assert.True(CanAccept(fixture));
        Assert.True(Tab(fixture));
        await writerReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Faults);
    }

    [Theory]
    [InlineData(EditableCommitMethod.CancelCompositionAndUnicode)]
    [InlineData(EditableCommitMethod.CancelCompositionAndPaste)]
    public async Task A_verified_inline_anchor_waits_for_translation_and_repeated_Tab_writes_once(EditableCommitMethod method)
    {
        var observer = new Observer();
        var baseline = Context(method: method);
        using var fixture = Fixture(observer, baseline);
        var translation = Completion<string>();
        var translating = Completion<bool>();
        var writing = Completion<bool>();
        var finishWrite = Completion<bool>();
        int writes = 0;
        fixture.TranslateAsync = (_, _) => { translating.TrySetResult(true); return translation.Task; };
        fixture.ReplaceAsync = async (expected, anchor, text, token, current, finishCaret) =>
        {
            Assert.NotNull(anchor);
            Assert.Equal(baseline.PlainText, anchor.Text);
            Assert.Equal(method, expected.CommitMethod);
            Assert.Equal("belongs to", text);
            Assert.False(token.IsCancellationRequested);
            Assert.True(current());
            Assert.NotNull(finishCaret);
            Assert.True(finishCaret());
            Interlocked.Increment(ref writes);
            writing.TrySetResult(true);
            await finishWrite.Task.WaitAsync(token);
            var after = baseline with { Value = "basebelongs to", DocumentText = "basebelongs to", SelectionStartUtf16 = 14, SelectionEndUtf16 = 14 };
            fixture.Context = after;
            return new(true, true, "HeadlessVerified", after, true);
        };
        await BindAsync(fixture);
        fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
        fixture.SetContext(baseline with { Value = "baseshuyu", DocumentText = "baseshuyu", SelectionStartUtf16 = 9, SelectionEndUtf16 = 9 });
        await ObserveAsync(fixture, observer);
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(CanAccept(fixture));
        Assert.True(Tab(fixture));
        Assert.True(Tab(fixture));
        Assert.Equal(0, Volatile.Read(ref writes));
        translation.SetResult("belongs to");
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Tab(fixture));
        finishWrite.SetResult(true);
        await WaitForStateAsync(fixture, x => x.Observation is null);
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.False(CanAccept(fixture));
        Assert.Empty(fixture.Faults);
    }

    [Theory]
    [InlineData(CompositionState.Unknown, true)]
    [InlineData(CompositionState.Inactive, true)]
    [InlineData(CompositionState.Active, true)]
    public async Task Generic_overlay_can_request_verification_without_pretending_cancellation_is_proven(
        CompositionState compositionState, bool expectedCanAccept)
    {
        var observer = new Observer();
        var baseline = Context() with { AllowsOverlayComposition = true, CompositionState = CompositionState.Inactive };
        using var fixture = Fixture(observer, baseline);
        var writerReached = Completion<bool>();
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            writerReached.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(false, false, "HeadlessCancellationNotProven"));
        };
        await BindAsync(fixture);
        fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
        fixture.SetContext(baseline with { CompositionState = compositionState });
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, x => x.Translation == "English");
        Assert.Equal(expectedCanAccept, CanAccept(fixture));
        Assert.True(Tab(fixture));
        await writerReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.Faults);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("pointer")]
    [InlineData("focus")]
    public async Task Input_pointer_or_focus_change_discards_a_late_preview(string mutation)
    {
        var observer = new Observer();
        using var fixture = Fixture(observer, Context(canReplace: false, method: EditableCommitMethod.None));
        var translation = Completion<string>();
        var translating = Completion<bool>();
        var canceled = Completion<bool>();
        fixture.TranslateAsync = (_, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            translating.TrySetResult(true);
            return translation.Task; // Deliberately ignores cancellation until the test releases it.
        };
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long generation = fixture.Coordinator.State.Generation;
        switch (mutation)
        {
            case "key": fixture.Invoke("OnKeyStroke", new KeyStroke(0x41, true, KeyModifiers.None, false, 1)); break;
            case "pointer": fixture.Invoke("OnPointerStroke", new PointerStroke(PointerIntent.ButtonDown, 1)); break;
            case "focus": observer.LoseFocus(); break;
        }
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        translation.SetResult("must not reappear");
        Assert.True(fixture.Coordinator.State.Generation > generation);
        Assert.Null(fixture.Coordinator.State.Observation);
        Assert.False(CanAccept(fixture));
        Assert.False(Tab(fixture));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Acceptance_rechecks_live_context_even_without_a_hook_event()
    {
        var observer = new Observer();
        var baseline = Context();
        using var fixture = Fixture(observer, baseline);
        int writes = 0;
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(new EditableReplaceResult(true, true, "UnexpectedWriter"));
        };
        await BindAsync(fixture);
        fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
        var composing = baseline with { Value = "baseshuyu", DocumentText = "baseshuyu", SelectionStartUtf16 = 9, SelectionEndUtf16 = 9 };
        fixture.SetContext(composing);
        await ObserveAsync(fixture, observer);
        var preview = await WaitForStateAsync(fixture, x => x.Translation == "English");
        Assert.True(CanAccept(fixture));
        // Change only what the context reader sees; no keyboard/mouse/focus notification is sent.
        fixture.Context = composing with { SelectionStartUtf16 = 0, SelectionEndUtf16 = 0 };
        Assert.True(Tab(fixture));
        await WaitForStateAsync(fixture, x => x.Observation is null && x.Generation > preview.Generation);
        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.False(CanAccept(fixture));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Writer_user_notice_is_reported_without_exposing_the_technical_reason()
    {
        var observer = new Observer();
        var baseline = Context();
        using var fixture = Fixture(observer, baseline);
        var notice = Completion<string>();
        var statuses = new ConcurrentQueue<string>();
        const string userNotice = "译文暂保留在剪贴板，请核对后手动粘贴。";
        Action<string> statusHandler = status =>
        {
            statuses.Enqueue(status);
            if (status.Contains(userNotice, StringComparison.Ordinal)) notice.TrySetResult(status);
        };
        fixture.RuntimeType.GetEvent("StatusChanged")!.AddEventHandler(fixture.Runtime, statusHandler);
        fixture.ReplaceAsync = (_, _, _, _, _, _) => Task.FromResult(new EditableReplaceResult(false, false,
            "InternalTransportDiagnostic", WriteAttempted: true) { UserNotice = userNotice });
        await BindAsync(fixture);
        fixture.Invoke("OnKeyStroke", new KeyStroke(0x53, true, KeyModifiers.None, false, 1));
        fixture.SetContext(baseline with { Value = "baseshuyu", DocumentText = "baseshuyu", SelectionStartUtf16 = 9, SelectionEndUtf16 = 9 });
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, x => x.Translation == "English");
        Assert.True(Tab(fixture));
        Assert.Contains(userNotice, await notice.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(statuses, status => status.Contains("InternalTransportDiagnostic", StringComparison.Ordinal));
        Assert.Empty(fixture.Faults);
    }

    private sealed class Observer : ICandidateObserver
    {
        private long _version;
        public bool Enabled { get; private set; }
        public Task<bool> Ready => Task.FromResult(true);
        public bool IsRunning => Enabled;
        public bool SetEnabled(bool enabled) { Enabled = enabled; return true; }
        public bool IsCurrent(long version) => Enabled && version == Interlocked.Read(ref _version);
        public void NotifyInputChanged()
        {
            long version = Interlocked.Increment(ref _version);
            Invalidated?.Invoke(version);
        }
        public CandidateObservation Candidate()
        {
            long version = Interlocked.Increment(ref _version);
            return new(version, 1, Environment.ProcessId, "headless-editor", "属于", ["属于", "输入"], new(10, 20, 200, 30));
        }
        public Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(!cancellationToken.IsCancellationRequested && IsCurrent(snapshot.Version));
        public void LoseFocus() => FocusInvalidated?.Invoke();
        public event Action<long>? Invalidated;
        public event Action? FocusInvalidated;
        public event Action<CandidateObservation>? Observed { add { } remove { } }
        public event Action<long>? CandidatesCleared { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public void Dispose() => Enabled = false;
    }
}
