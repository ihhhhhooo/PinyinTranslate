using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

/// <summary>Managed lifecycle reproductions; no native runtime constructor or input/clipboard service is used.</summary>
public sealed class ImeTabAcceptanceRegressionTests
{
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static EditableContextSnapshot Editor(string text = "base") => new()
    {
        ForegroundWindow = 1, ForegroundProcessId = Environment.ProcessId, FocusProcessId = Environment.ProcessId,
        FocusRuntimeId = "tab-lifecycle-editor", AutomationId = "tab-lifecycle-editor", FrameworkId = "Synthetic", ClassName = "Edit",
        Value = text, DocumentText = text, HasValuePattern = true, HasTextPattern = true, HasCompleteText = true,
        HasSingleSelection = true, SelectionStartUtf16 = text.Length, SelectionEndUtf16 = text.Length,
        IsReadOnly = false, IsReadOnlyKnown = true, CanPreview = true, CanReplace = true,
        CommitMethod = EditableCommitMethod.CancelCompositionAndPaste, CompositionState = CompositionState.Unknown
    };

    private static HeadlessRuntimeFixture Fixture(Observer observer)
    {
        var fixture = new HeadlessRuntimeFixture((_, _, _) => observer);
        fixture.Enable();
        return fixture;
    }

    private static Task BindAsync(HeadlessRuntimeFixture fixture) =>
        fixture.InvokeAsync("BindAsync", fixture.Context!, 1L, CancellationToken.None);

    private static void Key(HeadlessRuntimeFixture fixture, uint key) =>
        fixture.Invoke("OnKeyStroke", new KeyStroke(key, true, KeyModifiers.None, false, 1));

    private static bool CanAccept(HeadlessRuntimeFixture fixture) =>
        (bool)fixture.RuntimeType.GetProperty("CanAcceptCurrent")!.GetValue(fixture.Runtime)!;

    private static bool Tab(HeadlessRuntimeFixture fixture) => (bool)fixture.Invoke("TryAcceptTab", (nint)1)!;

    private static BaselineAnchor VerifiedFakeBaseline(EditableContextSnapshot composing, EditableContextSnapshot afterEscape)
    {
        var recovered = new BaselineAnchor(afterEscape.PlainText, afterEscape.SelectionStartUtf16,
            EditableContextIdentity.From(afterEscape));
        Assert.True(EditableContextCapabilities.HasVerifiedCancellation(composing, afterEscape, recovered));
        return recovered;
    }

    private static async Task ObserveAsync(HeadlessRuntimeFixture fixture, Observer observer) =>
        await fixture.InvokeAsync("ObserveAsync", fixture.Get("_binding")!, observer.NextCandidate());

    private static async Task<CandidateTranslationState> WaitForStateAsync(HeadlessRuntimeFixture fixture,
        Func<CandidateTranslationState, bool> predicate)
    {
        var completion = Completion<CandidateTranslationState>();
        void Changed(CandidateTranslationState state) { if (predicate(state)) completion.TrySetResult(state); }
        fixture.Coordinator.Changed += Changed;
        try
        {
            var state = fixture.Coordinator.State;
            return predicate(state) ? state : await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { fixture.Coordinator.Changed -= Changed; }
    }

    [Fact]
    public async Task Startup_focus_notice_must_not_erase_a_captured_first_word_baseline_for_a_fresh_Tab()
    {
        var observer = new Observer(ready: false) { NotifyFocusOnEnable = true };
        using var fixture = Fixture(observer);
        var baseline = Editor();
        fixture.SetContext(baseline);
        var writing = Completion<bool>();
        int writes = 0;
        fixture.ReplaceAsync = (context, hypothesis, _, token, current, finishCaret) =>
        {
            Assert.Null(hypothesis); // Lifecycle invalidation removed it; the workflow must verify actual post-Esc evidence.
            Assert.Equal(baseline.PlainText, VerifiedFakeBaseline(context, baseline).Text);
            Assert.False(token.IsCancellationRequested);
            Assert.True(current());
            Assert.NotNull(finishCaret);
            Assert.True(finishCaret());
            Interlocked.Increment(ref writes);
            writing.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(true, true, "HeadlessAccepted"));
        };
        var binding = BindAsync(fixture);
        Assert.False(binding.IsCompleted);
        Key(fixture, 0x53); // The original text/caret is known before the first S.
        Assert.Equal("base", Assert.IsType<BaselineAnchor>(fixture.Get("_anchor")).Text);
        fixture.SetContext(Editor("baseshuyu"));
        observer.CompleteStartup(); // Simulate the helper's first focus notification during activation.
        await binding.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");

        Assert.True(CanAccept(fixture), "A missing pre-key anchor must reach guarded post-cancel verification, never bypass it.");
        Assert.True(Tab(fixture));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_focus_cycle_cancels_the_old_request_but_a_new_same_editor_Tab_can_use_the_original_baseline()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var baseline = Editor();
        fixture.SetContext(baseline);
        var oldTranslation = Completion<string>();
        var oldStarted = Completion<bool>();
        var oldCanceled = Completion<bool>();
        var writing = Completion<bool>();
        int translations = 0, writes = 0;
        fixture.TranslateAsync = (_, token) =>
        {
            if (Interlocked.Increment(ref translations) != 1) return Task.FromResult("fresh English");
            token.Register(() => oldCanceled.TrySetResult(true));
            oldStarted.TrySetResult(true);
            return oldTranslation.Task;
        };
        fixture.ReplaceAsync = (composing, hypothesis, text, token, current, _) =>
        {
            Assert.Null(hypothesis);
            Assert.Equal(baseline.PlainText, VerifiedFakeBaseline(composing, baseline).Text);
            Assert.Equal("fresh English", text);
            Assert.False(token.IsCancellationRequested);
            Assert.True(current());
            Interlocked.Increment(ref writes);
            writing.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(true, true, "HeadlessAccepted"));
        };
        await BindAsync(fixture);
        Key(fixture, 0x53);
        var composing = Editor("baseshuyu");
        fixture.SetContext(composing);
        await ObserveAsync(fixture, observer);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Tab(fixture));
        observer.LoseFocus();
        await oldCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Coordinator.State.Observation);
        Assert.Equal(0, Volatile.Read(ref writes));

        // New current evidence and a NEW physical Tab are required. The canceled
        // acceptance above must never migrate to this new candidate publication.
        fixture.SetContext(composing);
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "fresh English");
        oldTranslation.TrySetResult("obsolete translation");
        Assert.False(fixture.Coordinator.State.AcceptRequested);
        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.True(CanAccept(fixture), "The new explicit Tab may verify post-cancel evidence without reviving the canceled old request.");
        Assert.True(Tab(fixture));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task First_key_before_the_first_context_read_still_reaches_guarded_post_cancel_verification()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        int writerEntries = 0;
        var writing = Completion<bool>();
        fixture.ReplaceAsync = (composing, hypothesis, _, _, current, _) =>
        {
            Assert.Null(hypothesis);
            Assert.True(current());
            VerifiedFakeBaseline(composing, Editor());
            Interlocked.Increment(ref writerEntries);
            writing.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(true, true, "HeadlessPostCancelVerified"));
        };
        Assert.Null(fixture.Get("_context"));
        Key(fixture, 0x53); // Pointer/focus invalidation or cold start has left no cached context.
        Assert.Null(fixture.Get("_anchor"));
        fixture.SetContext(Editor("baseshuyu")); // First successful poll already includes pre-edit.
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        Assert.True(fixture.Context!.CanReplace); // This is not a control-capability failure.
        Assert.Null(fixture.Get("_anchor"));
        Assert.True(CanAccept(fixture));
        Assert.True(Tab(fixture));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref writerEntries));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_partial_pinyin_hypothesis_does_not_prevent_actual_post_cancel_baseline_recovery()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var actualPostEscapeBaseline = Editor();
        int writerEntries = 0, clipboardEntries = 0;
        var writerReturned = Completion<bool>();
        fixture.ReplaceAsync = (composing, anchor, _, _, _, _) =>
        {
            Interlocked.Increment(ref writerEntries);
            // Pure production proof, with a fake post-Esc snapshot. No Esc or
            // clipboard operation is performed by this test.
            Assert.NotNull(anchor);
            Assert.Equal("basesh", anchor.Text);
            Assert.False(EditableContextCapabilities.HasVerifiedCancellation(composing, actualPostEscapeBaseline, anchor));
            var recovered = VerifiedFakeBaseline(composing, actualPostEscapeBaseline);
            bool verified = recovered.Text == "base";
            if (verified) Interlocked.Increment(ref clipboardEntries);
            writerReturned.TrySetResult(verified);
            return Task.FromResult(new EditableReplaceResult(verified, verified,
                verified ? "HeadlessRecoveredBaseline" : "CompositionEndNotVerified", WriteAttempted: true));
        };
        Key(fixture, 0x53);
        Key(fixture, 0x48); // The cached context is still missing for S,H.
        fixture.SetContext(Editor("basesh"));
        await BindAsync(fixture);
        Key(fixture, 0x55); // U is mistaken for the first key; its baseline already contains SH.
        Assert.Equal("basesh", Assert.IsType<BaselineAnchor>(fixture.Get("_anchor")).Text);
        fixture.SetContext(Editor("baseshuyu"));
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        Assert.True(CanAccept(fixture));
        Assert.True(Tab(fixture));
        Assert.True(await writerReturned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForStateAsync(fixture, state => state.Observation is null);
        Assert.Equal(1, Volatile.Read(ref writerEntries));
        Assert.Equal(1, Volatile.Read(ref clipboardEntries));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Unexposed_generic_overlay_can_request_a_probe_but_unchanged_unknown_state_cannot_paste()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var baseline = EditableContextCapabilities.Evaluate(Editor());
        fixture.SetContext(baseline);
        int writerEntries = 0, clipboardEntries = 0;
        var writing = Completion<bool>();
        fixture.ReplaceAsync = (composing, hypothesis, _, _, current, _) =>
        {
            Interlocked.Increment(ref writerEntries);
            Assert.True(current());
            Assert.NotNull(hypothesis);
            if (EditableContextCapabilities.HasVerifiedCancellation(composing, baseline, hypothesis))
                Interlocked.Increment(ref clipboardEntries);
            writing.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(false, false, "CompositionEndNotVerified", WriteAttempted: true)
                { UserNotice = "无法确认拼音已取消，本次未粘贴。" });
        };
        await BindAsync(fixture);
        Key(fixture, 0x53);
        var original = Assert.IsType<BaselineAnchor>(fixture.Get("_anchor"));
        fixture.SetContext(baseline); // The IME overlay is excluded from UIA's document.
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        Assert.Equal(EditableCommitMethod.CancelCompositionAndPaste, baseline.CommitMethod);
        Assert.Equal(CompositionState.Unknown, baseline.CompositionState);
        Assert.True(baseline.CanReplace);
        Assert.False(original.TryMatch(baseline, out _));
        Assert.True(CanAccept(fixture));
        Assert.True(Tab(fixture));
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(fixture, state => state.Observation is null);
        Assert.Equal(1, Volatile.Read(ref writerEntries));
        Assert.Equal(0, Volatile.Read(ref clipboardEntries));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_publication_revoked_before_its_notification_waits_for_the_new_candidate_instead_of_losing_Tab()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        fixture.SetContext(Editor());
        var writing = Completion<bool>();
        int writes = 0;
        fixture.ReplaceAsync = (_, _, _, _, current, _) =>
        {
            Assert.True(current());
            Interlocked.Increment(ref writes);
            writing.TrySetResult(true);
            return Task.FromResult(new EditableReplaceResult(true, true, "HeadlessLatestCandidate"));
        };
        await BindAsync(fixture);
        Key(fixture, 0x53);
        fixture.SetContext(Editor("baseshuyu"));
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        Assert.NotNull(fixture.Get("_publication"));
        observer.RevokeWithoutNotification(); // Local source is stale; its queued Runtime callback has not run.
        Assert.True(Tab(fixture));
        Assert.NotNull(fixture.Get("_pendingTab"));
        Assert.Equal(0, Volatile.Read(ref writes));
        await ObserveAsync(fixture, observer);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(fixture, state => state.Observation is null);
        Assert.Null(fixture.Get("_pendingTab"));
        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Continuing_to_type_cancels_the_stale_candidate_Tab_ticket_before_the_next_publication()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        fixture.SetContext(Editor());
        int writes = 0;
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.FromResult(new EditableReplaceResult(true, true, "UnexpectedWriter"));
        };
        await BindAsync(fixture);
        Key(fixture, 0x53);
        fixture.SetContext(Editor("baseshuyu"));
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        observer.RevokeWithoutNotification();
        Assert.True(Tab(fixture));
        Assert.NotNull(fixture.Get("_pendingTab"));
        Key(fixture, 0x41);
        Assert.Null(fixture.Get("_pendingTab"));
        fixture.SetContext(Editor("baseshuyua"));
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        Assert.False(fixture.Coordinator.State.AcceptRequested);
        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Optional_UIA_metadata_changes_do_not_invalidate_the_same_text_and_caret()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        fixture.SetContext(Editor());
        await BindAsync(fixture);
        Key(fixture, 0x53);
        var composing = Editor("baseshuyu");
        fixture.SetContext(composing);
        await ObserveAsync(fixture, observer);
        var preview = await WaitForStateAsync(fixture, state => state.Translation == "English");
        fixture.Context = composing with
        {
            HasValuePattern = false, UsesDocumentText = true,
            CompositionState = CompositionState.Active, CapabilityReason = "MetadataChangedOnly"
        };
        Assert.True(await fixture.InvokeAsync<bool>("ValidateAsync", preview.Observation!, CancellationToken.None));
        Assert.NotNull(fixture.Coordinator.State.Observation);
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_rejected_Tab_reason_survives_the_same_pass_through_Tabs_input_invalidation()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        fixture.SetContext(Editor() with
        {
            CanReplace = false, IsReadOnly = true,
            CommitMethod = EditableCommitMethod.None, CapabilityReason = "ReadOnly"
        });
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await WaitForStateAsync(fixture, state => state.Translation == "English");
        var rejection = Completion<string>();
        Action<string> statusHandler = status =>
        {
            if (status.Contains("只读", StringComparison.Ordinal)) rejection.TrySetResult(status);
        };
        fixture.RuntimeType.GetEvent("StatusChanged")!.AddEventHandler(fixture.Runtime, statusHandler);
        try
        {
            // Hold the same managed gate until the hook's complete false-path
            // has run. The queued status cannot race ahead of OnKeyStroke(Tab).
            lock (fixture.Get("_gate")!)
            {
                long input = fixture.Get<long>("_inputVersion");
                Assert.False(Tab(fixture));
                Key(fixture, 0x09);
                Assert.True(fixture.Get<long>("_inputVersion") > input);
            }
            Assert.Contains("只读", await rejection.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(fixture.Coordinator.State.AcceptRequested);
            Assert.Empty(fixture.Faults);
        }
        finally { fixture.RuntimeType.GetEvent("StatusChanged")!.RemoveEventHandler(fixture.Runtime, statusHandler); }
    }

    private sealed class Observer : ICandidateObserver
    {
        private readonly TaskCompletionSource<bool> _ready = Completion<bool>();
        private long _version;
        public Observer(bool ready = true) { if (ready) _ready.TrySetResult(true); }
        public bool NotifyFocusOnEnable { get; init; }
        public bool Enabled { get; private set; }
        public Task<bool> Ready => _ready.Task;
        public bool IsRunning => Enabled;
        public void CompleteStartup() => _ready.TrySetResult(true);
        public bool SetEnabled(bool enabled)
        {
            Enabled = enabled;
            if (enabled && NotifyFocusOnEnable) FocusInvalidated?.Invoke();
            return true;
        }
        public bool IsCurrent(long version) => Enabled && Interlocked.Read(ref _version) == version;
        public CandidateObservation NextCandidate() => new(Interlocked.Increment(ref _version), 1,
            Environment.ProcessId, "tab-lifecycle-editor", "属于", ["属于", "输入"], new(10, 20, 200, 30));
        public void NotifyInputChanged()
        {
            long version = Interlocked.Increment(ref _version);
            Invalidated?.Invoke(version);
        }
        public Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(!cancellationToken.IsCancellationRequested && IsCurrent(snapshot.Version));
        public void LoseFocus() => FocusInvalidated?.Invoke();
        public void RevokeWithoutNotification() => Interlocked.Increment(ref _version);
        public event Action<long>? Invalidated;
        public event Action? FocusInvalidated;
        public event Action<CandidateObservation>? Observed { add { } remove { } }
        public event Action<long>? CandidatesCleared { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
        public void Dispose() { Enabled = false; _ready.TrySetResult(false); }
    }
}
