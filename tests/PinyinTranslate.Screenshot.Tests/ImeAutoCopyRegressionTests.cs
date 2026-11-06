using System.Collections.Concurrent;
using PinyinTranslate.Input;

namespace PinyinTranslate.Screenshot.Tests;

public sealed class ImeAutoCopyRegressionTests
{
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static EditableContextSnapshot Editor(bool writable = false) => new()
    {
        ForegroundWindow = 1, ForegroundProcessId = Environment.ProcessId, FocusProcessId = Environment.ProcessId,
        FocusRuntimeId = "auto-copy-editor", AutomationId = "auto-copy-editor", FrameworkId = "Synthetic", ClassName = "Edit",
        Value = writable ? "base" : "", DocumentText = writable ? "base" : "",
        HasValuePattern = writable, HasTextPattern = writable, HasCompleteText = writable,
        HasSingleSelection = writable, SelectionStartUtf16 = writable ? 4 : -1, SelectionEndUtf16 = writable ? 4 : -1,
        IsReadOnly = !writable, IsReadOnlyKnown = true, CanPreview = true, CanReplace = writable,
        CommitMethod = writable ? EditableCommitMethod.CancelCompositionAndPaste : EditableCommitMethod.None
    };

    private static HeadlessRuntimeFixture Fixture(Observer observer, bool writable = false)
    {
        var fixture = new HeadlessRuntimeFixture((_, _, _) => observer, copyOnly: true);
        fixture.Enable();
        fixture.SetContext(Editor(writable));
        return fixture;
    }

    private static Task BindAsync(HeadlessRuntimeFixture fixture) =>
        fixture.InvokeAsync("BindAsync", fixture.Context!, 1L, CancellationToken.None);

    private static Task ObserveAsync(HeadlessRuntimeFixture fixture, Observer observer, string candidate = "属于") =>
        fixture.InvokeAsync("ObserveAsync", fixture.Get("_binding")!, observer.Candidate(candidate));

    private static void Key(HeadlessRuntimeFixture fixture, uint key) =>
        fixture.Invoke("OnKeyStroke", new KeyStroke(key, true, KeyModifiers.None, false, 1));

    private static string Hint(HeadlessRuntimeFixture fixture) =>
        (string)fixture.RuntimeType.GetProperty("CurrentAcceptanceHint")!.GetValue(fixture.Runtime)!;

    private static bool CanAccept(HeadlessRuntimeFixture fixture) =>
        (bool)fixture.RuntimeType.GetProperty("CanAcceptCurrent")!.GetValue(fixture.Runtime)!;

    private static bool Tab(HeadlessRuntimeFixture fixture) => (bool)fixture.Invoke("TryAcceptTab", (nint)1)!;

    private static void Copied(HeadlessRuntimeFixture fixture, Action<string> handler) =>
        fixture.RuntimeType.GetEvent("TranslationCopied")!.AddEventHandler(fixture.Runtime, handler);

    private static async Task<string> WaitForHintAsync(HeadlessRuntimeFixture fixture, string fragment)
    {
        var completion = Completion<string>();
        void Changed(CandidateTranslationState _)
        {
            string hint = Hint(fixture);
            if (hint.Contains(fragment, StringComparison.Ordinal)) completion.TrySetResult(hint);
        }
        var notification = fixture.RuntimeType.GetEvent("PreviewChanged")!;
        Action<CandidateTranslationState> subscriber = Changed;
        notification.AddEventHandler(fixture.Runtime, subscriber);
        try
        {
            Changed(fixture.Coordinator.State);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { notification.RemoveEventHandler(fixture.Runtime, subscriber); }
    }

    [Fact]
    public async Task Preview_only_editor_without_text_patterns_or_anchor_still_copies_the_translation()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        string? clipboard = null;
        int legacyWrites = 0;
        fixture.CopyAsync = (text, current, token) =>
        {
            Assert.True(current());
            Assert.False(token.IsCancellationRequested);
            clipboard = text;
            return Task.FromResult(new TranslationClipboardCopyResult(true, true, "Copied"));
        };
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref legacyWrites);
            return Task.FromResult(new EditableReplaceResult(false, false, "UnexpectedLegacyWriter"));
        };
        Assert.Null(fixture.Get("_contexts"));
        Assert.Null(fixture.Get("_keyboard"));
        Assert.Null(fixture.Get("_pointer"));
        Assert.False(fixture.Coordinator.HasAcceptor);
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await WaitForHintAsync(fixture, "已复制英文");
        Assert.Equal("English", clipboard);
        Assert.Single(fixture.CopyRequests);
        Assert.Null(fixture.Get("_anchor"));
        Assert.False(CanAccept(fixture));
        Assert.False(Tab(fixture));
        Assert.Equal(0, Volatile.Read(ref legacyWrites));
        Assert.False(fixture.Coordinator.State.CanRequestAccept);
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Repeated_ready_notifications_copy_once_and_subscribers_cannot_block_the_runtime_gate()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var copied = Completion<string>();
        int notifications = 0;
        fixture.CopyAsync = (_, current, _) =>
        {
            // A different managed thread must be able to enter the guard while
            // the fake clipboard operation runs: no Runtime gate surrounds it.
            var guard = Task.Run(current);
            Assert.True(guard.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(guard.Result);
            return Task.FromResult(new TranslationClipboardCopyResult(true, true, "Copied"));
        };
        Copied(fixture, _ => throw new InvalidOperationException("subscriber failure"));
        Copied(fixture, text =>
        {
            var read = Task.Run(() => Hint(fixture));
            Assert.True(read.Wait(TimeSpan.FromSeconds(2)));
            Interlocked.Increment(ref notifications);
            copied.TrySetResult(text);
        });
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        Assert.Equal("English", await copied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForHintAsync(fixture, "已复制英文");
        var ready = fixture.Coordinator.State;
        fixture.Invoke("OnStateChanged", ready);
        fixture.Invoke("OnStateChanged", ready);
        Assert.Single(fixture.CopyRequests);
        Assert.Equal(1, Volatile.Read(ref notifications));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_late_old_translation_never_replaces_or_copies_over_the_new_candidate()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var old = Completion<string>();
        var translating = Completion<bool>();
        var canceled = Completion<bool>();
        fixture.TranslateAsync = (text, token) =>
        {
            if (text == "新候选") return Task.FromResult("new English");
            token.Register(() => canceled.TrySetResult(true));
            translating.TrySetResult(true);
            return old.Task;
        };
        fixture.CopyAsync = (_, current, _) => Task.FromResult(new TranslationClipboardCopyResult(current(), true, "Copied"));
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer, "旧候选");
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("正在翻译", Hint(fixture));
        Key(fixture, 0x41);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveAsync(fixture, observer, "新候选");
        await WaitForHintAsync(fixture, "已复制英文");
        old.SetResult("obsolete English");
        Assert.Equal("new English", Assert.Single(fixture.CopyRequests).Text);
        Assert.Equal("new English", fixture.Coordinator.State.Translation);
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_canceled_translations_late_fault_does_not_replace_a_new_copied_presentation()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var old = Completion<string>();
        var translating = Completion<bool>();
        var canceled = Completion<bool>();
        var faultObserved = Completion<bool>();
        var statuses = new ConcurrentQueue<string>();
        fixture.TranslateAsync = (text, token) =>
        {
            if (text == "新候选") return Task.FromResult("new English");
            token.Register(() => canceled.TrySetResult(true));
            translating.TrySetResult(true);
            return old.Task;
        };
        fixture.CopyAsync = (_, current, _) => Task.FromResult(new TranslationClipboardCopyResult(current(), true, "Copied"));
        Action<string> status = statuses.Enqueue;
        fixture.RuntimeType.GetEvent("StatusChanged")!.AddEventHandler(fixture.Runtime, status);
        // The copy-only fixture subscribes the real Runtime.OnCoordinatorFaulted
        // before this observer, so seeing this signal proves the production handler ran.
        fixture.Coordinator.Faulted += error =>
        {
            if (error.Message == "obsolete failure from A") faultObserved.TrySetResult(true);
        };
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer, "旧候选");
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Key(fixture, 0x41);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveAsync(fixture, observer, "新候选");
        await WaitForHintAsync(fixture, "已复制英文");
        old.SetException(new InvalidOperationException("obsolete failure from A"));
        await faultObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("已复制英文", Hint(fixture));
        Assert.Equal("Copied", fixture.Get("_copyState")!.ToString());
        Assert.Equal("new English", Assert.Single(fixture.CopyRequests).Text);
        Assert.DoesNotContain(statuses, value => value.Contains("失败", StringComparison.Ordinal) || value.Contains("obsolete failure", StringComparison.Ordinal));
        Assert.Equal("obsolete failure from A", Assert.Single(fixture.Faults).Message);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("focus")]
    [InlineData("mode")]
    public async Task An_inflight_copy_is_revoked_and_its_late_success_is_never_reported(string mutation)
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var entered = Completion<bool>();
        var canceled = Completion<bool>();
        var result = Completion<TranslationClipboardCopyResult>();
        int copied = 0;
        Copied(fixture, _ => Interlocked.Increment(ref copied));
        fixture.CopyAsync = (_, _, token) =>
        {
            token.Register(() => canceled.TrySetResult(true));
            entered.TrySetResult(true);
            return result.Task; // Model an OS request which no longer cooperates with cancellation.
        };
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForHintAsync(fixture, "正在复制");
        var request = Assert.Single(fixture.CopyRequests);
        Assert.True(request.StillCurrent());
        switch (mutation)
        {
            case "key": Key(fixture, 0x41); break;
            case "focus": observer.LoseFocus(); break;
            case "mode":
                lock (fixture.Get("_gate")!)
                {
                    fixture.Set("_enabled", 0);
                    fixture.Set("_epoch", 2L);
                    fixture.Invoke("InvalidateOperationsLocked");
                }
                break;
        }
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.False(request.StillCurrent());
        result.SetResult(new(true, true, "Copied"));
        Assert.NotEqual("Copied", fixture.Get("_copyState")!.ToString());
        Assert.DoesNotContain("已复制英文", Hint(fixture));
        Assert.Equal(0, Volatile.Read(ref copied));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Esc_and_manual_Tab_leave_a_completed_copy_untouched_and_never_enter_the_legacy_writer()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer, writable: true);
        string? clipboard = null;
        int legacyWrites = 0;
        var copied = Completion<string>();
        Copied(fixture, text => copied.TrySetResult(text));
        fixture.CopyAsync = (text, current, _) =>
        {
            Assert.True(current()); clipboard = text;
            return Task.FromResult(new TranslationClipboardCopyResult(true, true, "Copied"));
        };
        fixture.ReplaceAsync = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref legacyWrites);
            return Task.FromResult(new EditableReplaceResult(false, false, "UnexpectedLegacyWriter"));
        };
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        Assert.Equal("English", await copied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Tab(fixture));
        Key(fixture, 0x09); // Ordinary pass-through Tab is just an invalidation observation.
        Key(fixture, 0x1B); // The user, not Runtime, presses Esc.
        Assert.Equal("English", clipboard);
        Assert.Single(fixture.CopyRequests);
        Assert.Equal(0, Volatile.Read(ref legacyWrites));
        Assert.False(CanAccept(fixture));
        Assert.False(fixture.Coordinator.HasAcceptor);
        Assert.False(string.IsNullOrWhiteSpace(Hint(fixture)));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task A_copy_failure_shows_a_reason_and_does_not_retry_the_same_generation()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        int copied = 0;
        Copied(fixture, _ => Interlocked.Increment(ref copied));
        fixture.CopyAsync = (_, _, _) => Task.FromResult(new TranslationClipboardCopyResult(false, false, "ClipboardBusy"));
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        Assert.Contains("ClipboardBusy", await WaitForHintAsync(fixture, "复制失败"));
        fixture.Invoke("OnStateChanged", fixture.Coordinator.State);
        fixture.Invoke("OnStateChanged", fixture.Coordinator.State);
        Assert.Single(fixture.CopyRequests);
        Assert.Equal(0, Volatile.Read(ref copied));
        Assert.Empty(fixture.Faults);
    }

    [Fact]
    public async Task Foreground_change_is_visible_to_the_worker_guard_before_any_UIA_notification()
    {
        var observer = new Observer();
        using var fixture = Fixture(observer);
        var entered = Completion<bool>();
        var result = Completion<TranslationClipboardCopyResult>();
        int copied = 0;
        Copied(fixture, _ => Interlocked.Increment(ref copied));
        fixture.CopyAsync = (_, _, _) => { entered.TrySetResult(true); return result.Task; };
        await BindAsync(fixture);
        await ObserveAsync(fixture, observer);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = Assert.Single(fixture.CopyRequests);
        fixture.ForegroundWindow = 2; // Pure injected getter; no native foreground access.
        Assert.False(request.StillCurrent());
        result.SetResult(new(true, true, "Copied"));
        Assert.DoesNotContain("已复制英文", Hint(fixture));
        Assert.Equal(0, Volatile.Read(ref copied));
        Assert.Empty(fixture.Faults);
    }

    private sealed class Observer : ICandidateObserver
    {
        private long _version;
        public Task<bool> Ready => Task.FromResult(true);
        public bool Enabled { get; private set; }
        public bool IsRunning => Enabled;
        public bool SetEnabled(bool enabled) { Enabled = enabled; return true; }
        public bool IsCurrent(long version) => Enabled && Interlocked.Read(ref _version) == version;
        public CandidateObservation Candidate(string text) => new(Interlocked.Increment(ref _version), 1,
            Environment.ProcessId, "auto-copy-editor", text, [text, "其他"], new(10, 20, 200, 30));
        public void NotifyInputChanged()
        {
            long version = Interlocked.Increment(ref _version);
            Invalidated?.Invoke(version);
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
