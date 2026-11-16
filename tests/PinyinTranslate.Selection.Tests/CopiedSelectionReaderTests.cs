using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// Pure gestures and an injected clipboard API. No system clipboard, hooks,
// native provider, window or input is accessed, including the blocking test.
public sealed class CopiedSelectionReaderTests
{
    [Fact]
    public async Task A_source_owned_copy_can_be_read_again_when_the_terminal_does_not_change_the_sequence()
    {
        var api = new FakeApi { ReadOverride = _ => new(10, CopySelectionStatus.Success, "selected terminal text") };
        api.Metadata = api.Metadata with { Sequence = 10 };
        long time = 0;
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => { time += 250; return time < 2000; }, clock: () => time);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Success, result.Status);
        Assert.Equal("selected terminal text", result.Selection!.Text);
        Assert.True(result.Selection.SourceOwnedBaseline);
    }

    [Fact]
    public async Task Waiting_for_the_copy_prefers_new_text_over_the_source_apps_previous_clipboard()
    {
        long time = 0;
        var api = new FakeApi(); api.Metadata = api.Metadata with { Sequence = 10 };
        api.ReadOverride = count =>
        {
            if (count < 3) return new(10, CopySelectionStatus.Success, "previous selection");
            api.Metadata = api.Metadata with { Sequence = 11 };
            return new(11, CopySelectionStatus.Success, "new selection");
        };
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => { time += 250; return true; }, clock: () => time);
        var copy = (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection!;
        Assert.Equal("new selection", copy.Text); Assert.False(copy.SourceOwnedBaseline);
    }

    [Fact]
    public async Task A_repeat_copy_never_uses_a_sequence_written_by_our_own_translation()
    {
        long time = 0;
        var api = new FakeApi { ReadOverride = _ => new(10, CopySelectionStatus.Success, "own translation") };
        api.Metadata = api.Metadata with { Sequence = 10 };
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => { time += 250; return time < 1500; },
            ignoreOwnSequence: sequence => sequence == 10, clock: () => time);
        Assert.Null((await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_physical_copy_is_sufficient_even_without_a_recent_selection_gesture(bool staleGesture)
    {
        long time = 1000;
        var tracker = new CopySelectionIntentTracker(() => time);
        if (staleGesture) { tracker.Observe(Gesture()); time += 16000; }
        Assert.Equal(new CopySelectionRequest(100, 42, 10), tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true)));
        Assert.Null(tracker.Observe(Key(0x43, false, SelectionActivityModifiers.Control)));
        Assert.Null(tracker.Observe(Key(0x11, false)));
    }

    [Fact]
    public async Task A_delayed_renderer_can_advance_the_sequence_when_text_is_requested()
    {
        var api = new FakeApi();
        api.Metadata = api.Metadata with { Sequence = 10 };
        api.BeforeRead = () => api.Metadata = api.Metadata with { Sequence = 11 };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Success, result.Status);
        Assert.Equal("new text 😀", result.Selection!.Text);
    }

    [Fact]
    public async Task Delayed_external_rendering_can_replace_a_baseline_written_by_this_app()
    {
        var api = new FakeApi(); api.Metadata = api.Metadata with { Sequence = 10 };
        api.BeforeRead = () => api.Metadata = api.Metadata with { Sequence = 11 };
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => false, ignoreOwnSequence: sequence => sequence == 10);
        Assert.Equal(CopySelectionStatus.Success,
            (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_copy_that_publishes_text_after_a_temporary_missing_format_is_retried()
    {
        var api = new FakeApi { ReadOverride = count => count == 1 ? new(11, CopySelectionStatus.NoText) : new(11, CopySelectionStatus.Success, "documentation") };
        using var reader = Reader(api, retry: true);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal("documentation", result.Selection!.Text); Assert.Equal(2, api.TextReads);
    }

    [Fact]
    public async Task A_busy_copy_is_not_abandoned_by_the_old_fixed_attempt_limit()
    {
        var api = new FakeApi { ReadOverride = count => count <= 150 ? null : new(11, CopySelectionStatus.Success, "ready later") };
        using var reader = Reader(api, retry: true);
        Assert.Equal("ready later", (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection!.Text);
    }

    private static SelectionActivity Key(uint key, bool down, SelectionActivityModifiers modifiers = SelectionActivityModifiers.None,
        bool copySequence = false) => new(down ? SelectionActivityKind.KeyDown : SelectionActivityKind.KeyUp, 100, 0, 42,
            VirtualKey: key, Modifiers: modifiers) { HasCopySequence = copySequence, BeforeCopySequence = copySequence ? 10u : 0u };
    private static SelectionActivity Gesture() => new(SelectionActivityKind.PointerUp, 100, 110, 43,
        MouseButton: 1, CompletesSelectionGesture: true) { TargetRootWindow = 100 };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Copy_is_captured_on_press_and_not_repeated_by_held_keys_or_releases(bool terminalShift, bool controlReleasedFirst)
    {
        var tracker = new CopySelectionIntentTracker(() => 1000);
        Assert.Null(tracker.Observe(Gesture())); // Child renderer PID may differ from the host.
        var modifiers = SelectionActivityModifiers.Control | (terminalShift ? SelectionActivityModifiers.Shift : 0);
        Assert.Null(tracker.Observe(Key(0x11, true, modifiers)));
        Assert.Equal(new CopySelectionRequest(100, 42, 10), tracker.Observe(Key(0x43, true, modifiers, true)));
        Assert.Null(tracker.Observe(Key(0x43, true, modifiers, true) with { IsRepeat = true }));
        var afterControl = terminalShift ? SelectionActivityModifiers.Shift : SelectionActivityModifiers.None;
        if (controlReleasedFirst)
        {
            Assert.Null(tracker.Observe(Key(0x11, false, afterControl)));
            Assert.Null(tracker.Observe(Key(0x43, false, afterControl)));
        }
        else
        {
            Assert.Null(tracker.Observe(Key(0x43, false, modifiers)));
            Assert.Null(tracker.Observe(Key(0x11, false, afterControl)));
        }
        if (terminalShift) Assert.Null(tracker.Observe(Key(0x10, false)));
        Assert.False(tracker.HasActiveChord); Assert.Null(tracker.Observe(Key(0x11, false)));
    }

    [Theory]
    [InlineData("injected")]
    [InlineData("no-baseline")]
    [InlineData("alt")]
    [InlineData("stale-copy")]
    public void A_copy_requires_a_physical_key_and_a_fresh_captured_baseline(string scenario)
    {
        long time = 1000;
        var tracker = new CopySelectionIntentTracker(() => time);
        var key = Key(0x43, true, SelectionActivityModifiers.Control, true);
        if (scenario == "injected") key = key with { IsInjected = true };
        if (scenario == "no-baseline") key = key with { HasCopySequence = false };
        if (scenario == "alt") key = key with { Modifiers = SelectionActivityModifiers.Control | SelectionActivityModifiers.Alt };
        if (scenario == "stale-copy") { time = 17000; key = key with { OccurredAtMilliseconds = 1000 }; }
        Assert.Null(tracker.Observe(key));
        Assert.Null(tracker.Observe(Key(0x43, false, SelectionActivityModifiers.Control)));
        Assert.Null(tracker.Observe(Key(0x11, false)));
    }

    [Fact]
    public void A_keyboard_selection_can_arm_copy_and_a_later_real_key_revokes_the_intent()
    {
        var tracker = new CopySelectionIntentTracker(() => 1000);
        tracker.Observe(Key(0x41, false, SelectionActivityModifiers.Control) with { CompletesSelectionGesture = true });
        tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true));
        tracker.Observe(Key(0x58, true));
        Assert.Null(tracker.Observe(Key(0x43, false)));
        Assert.False(tracker.HasActiveChord);
    }

    [Fact]
    public void A_missing_release_does_not_prevent_the_next_physical_copy_from_starting()
    {
        long time = 1000;
        var tracker = new CopySelectionIntentTracker(() => time);
        tracker.Observe(Gesture()); tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true));
        Assert.True(tracker.HasActiveChord);
        time += 16000;
        Assert.NotNull(tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true)));
        Assert.Null(tracker.Observe(Key(0x43, false)));
    }

    [Theory]
    [InlineData("own-writer")]
    [InlineData("password")]
    [InlineData("other-window")]
    public async Task Old_clipboard_program_copies_and_wrong_scopes_never_read_text(string scenario)
    {
        var api = new FakeApi();
        api.Metadata = scenario switch
        {
            "own-writer" => api.Metadata with { OwnerProcessId = api.OwnProcessId },
            "password" => api.Metadata with { ProtectedFocus = true },
            _ => api.Metadata with { ForegroundWindow = 200 }
        };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.NotEqual(CopySelectionStatus.Success, result.Status); Assert.Null(result.Selection); Assert.Equal(0, api.TextReads);
    }

    [Fact]
    public async Task Unchanged_text_is_not_reused_before_the_application_has_had_time_to_copy()
    {
        var api = new FakeApi { ReadOverride = _ => new(10, CopySelectionStatus.Success, "old clipboard text") };
        api.Metadata = api.Metadata with { Sequence = 10 };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.NoNewText, result.Status);
        Assert.Null(result.Selection); Assert.Equal(1, api.TextReads);
    }

    [Fact]
    public async Task A_fresh_copy_from_a_clipboard_broker_does_not_need_to_own_the_foreground_window()
    {
        var api = new FakeApi(); api.Metadata = api.Metadata with { OwnerProcessId = 99, OwnerRoot = 900 };
        using var reader = Reader(api);
        Assert.Equal(CopySelectionStatus.Success,
            (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_captured_zero_baseline_can_accept_a_later_nonzero_clipboard_sequence()
    {
        var api = new FakeApi(); using var reader = Reader(api);
        Assert.Equal(CopySelectionStatus.Success,
            (await reader.ReadAfterCopyAsync(new(100, 42, 0), () => true, CancellationToken.None)).Status);
        var tracker = new CopySelectionIntentTracker(() => 1000);
        Assert.Equal(0u, tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true) with { BeforeCopySequence = 0 })!.BeforeSequence);
    }

    [Fact]
    public async Task A_new_copy_from_a_renderer_in_the_same_foreground_root_is_read_once_and_preserves_the_text()
    {
        var api = new FakeApi(); api.Metadata = api.Metadata with { OwnerProcessId = 43, OwnerRoot = 100 };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Success, result.Status); Assert.Equal("new text 😀", result.Selection!.Text); Assert.Equal(1, api.TextReads);
        api.Metadata = api.Metadata with { Sequence = 12, OwnerProcessId = 7 };
        Assert.True(reader.IsCurrent(result.Selection)); // Captured text stays immutable; copying the result is allowed.
        api.Metadata = api.Metadata with { ForegroundWindow = 200 };
        Assert.False(reader.IsCurrent(result.Selection));
    }

    [Fact]
    public async Task An_own_write_with_a_destroyed_owner_cannot_be_mistaken_for_a_new_external_copy()
    {
        var api = new FakeApi(); api.Metadata = api.Metadata with { OwnerProcessId = 0 };
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => false, ignoreOwnSequence: sequence => sequence == 11);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.NoNewText, result.Status); Assert.Null(result.Selection); Assert.Equal(0, api.TextReads);
    }

    [Fact]
    public async Task Busy_clipboard_retries_within_the_same_copy_instead_of_reading_an_old_value()
    {
        var api = new FakeApi { ReadOverride = count => count < 3 ? null : new(11, CopySelectionStatus.Success, "copied") };
        using var reader = Reader(api, retry: true);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal("copied", result.Selection!.Text); Assert.Equal(3, api.TextReads);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("cancelled")]
    public async Task Changes_while_reading_drop_the_text(string change)
    {
        bool current = true;
        var api = new FakeApi();
        api.BeforeRead = () =>
        {
            if (change == "window") api.Metadata = api.Metadata with { ForegroundWindow = 200 };
            if (change == "cancelled") current = false;
        };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => current, CancellationToken.None);
        Assert.Null(result.Selection); Assert.NotEqual(CopySelectionStatus.Success, result.Status);
    }

    [Fact]
    public async Task A_valid_text_snapshot_survives_subsequent_clipboard_format_updates()
    {
        var api = new FakeApi(); api.BeforeRead = () => api.Metadata = api.Metadata with { Sequence = 12 };
        using var reader = Reader(api);
        Assert.Equal("new text 😀", (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection!.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(7)]
    public async Task Unchanged_text_owned_by_another_app_or_unknown_owner_is_not_used_as_terminal_selection(int owner)
    {
        long time = 0;
        var api = new FakeApi { ReadOverride = _ => new(10, CopySelectionStatus.Success, "unrelated clipboard") };
        api.Metadata = api.Metadata with { Sequence = 10, OwnerProcessId = owner };
        using var reader = new WindowsCopiedSelectionReader(api, wait: (_, _) => { time += 250; return time < 1250; }, clock: () => time);
        Assert.Null((await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Unicode_clipboard_text_allows_allocation_padding_without_reading_past_the_buffer(int padding)
    {
        byte[] text = System.Text.Encoding.Unicode.GetBytes("终端 😀\0");
        nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(text.Length + padding);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(text, 0, buffer, text.Length);
            for (int i = 0; i < padding; i++) System.Runtime.InteropServices.Marshal.WriteByte(buffer, text.Length + i, 0xFF);
            var result = WindowsCopiedSelectionReader.ReadUnicodeText(buffer, (nuint)(text.Length + padding), 11, 4000);
            Assert.Equal(CopySelectionStatus.Success, result.Status); Assert.Equal("终端 😀", result.Text);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(0)]
    public async Task Invalid_text_is_rejected_before_translation(int value)
    {
        var api = new FakeApi { ReadOverride = _ => new(11, CopySelectionStatus.Success, new string((char)value, 1)) };
        using var reader = Reader(api);
        Assert.Null((await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Selection);
    }

    [Theory]
    [InlineData(CopySelectionStatus.NoText)]
    [InlineData(CopySelectionStatus.TooLong)]
    public async Task Non_text_and_oversized_copies_return_a_terminal_status(CopySelectionStatus status)
    {
        var api = new FakeApi { ReadOverride = _ => new(11, status) };
        using var reader = Reader(api);
        Assert.Equal(status, (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_blocked_injected_api_is_serialized_and_waiters_time_out_without_accumulating_reads()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var api = new FakeApi { BeforeRead = () => { entered.Set(); release.Wait(); } };
        using var reader = new WindowsCopiedSelectionReader(api, TimeSpan.FromMilliseconds(200), (_, _) => false);
        try
        {
            var first = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(CopySelectionStatus.TimedOut, (await first.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.Equal(CopySelectionStatus.TimedOut, (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
            Assert.Equal(1, api.TextReads);
        }
        finally { release.Set(); }
    }

    private static WindowsCopiedSelectionReader Reader(FakeApi api, bool retry = false) => new(api, wait: (_, token) => retry && !token.IsCancellationRequested);
    private sealed class FakeApi : ICopyClipboardReadApi
    {
        public int OwnProcessId => 7;
        public CopyClipboardMetadata Metadata = new(100, 42, 11, 42);
        public int TextReads;
        public Action? BeforeRead;
        public Func<int, CopyClipboardText?>? ReadOverride;
        public CopyClipboardMetadata GetMetadata() => Metadata;
        public CopyClipboardText? TryReadText(int maximumLength)
        {
            int count = Interlocked.Increment(ref TextReads); BeforeRead?.Invoke();
            return ReadOverride is { } read ? read(count) : new(11, CopySelectionStatus.Success, "new text 😀");
        }
    }
}
