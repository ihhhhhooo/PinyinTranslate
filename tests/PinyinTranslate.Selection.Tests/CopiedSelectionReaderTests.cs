using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// Pure gestures and an injected clipboard API. No system clipboard, hooks,
// native provider, window or input is accessed, including the blocking test.
public sealed class CopiedSelectionReaderTests
{
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
    public void Copy_is_captured_once_after_C_and_all_modifiers_are_released(bool terminalShift, bool controlReleasedFirst)
    {
        var tracker = new CopySelectionIntentTracker(() => 1000);
        Assert.Null(tracker.Observe(Gesture())); // Child renderer PID may differ from the host.
        var modifiers = SelectionActivityModifiers.Control | (terminalShift ? SelectionActivityModifiers.Shift : 0);
        Assert.Null(tracker.Observe(Key(0x11, true, modifiers)));
        Assert.Null(tracker.Observe(Key(0x43, true, modifiers, true)));
        Assert.Null(tracker.Observe(Key(0x43, true, modifiers, true) with { IsRepeat = true }));
        var afterControl = terminalShift ? SelectionActivityModifiers.Shift : SelectionActivityModifiers.None;
        CopySelectionRequest? result;
        if (controlReleasedFirst)
        {
            Assert.Null(tracker.Observe(Key(0x11, false, afterControl)));
            result = tracker.Observe(Key(0x43, false, afterControl));
        }
        else
        {
            Assert.Null(tracker.Observe(Key(0x43, false, modifiers)));
            result = tracker.Observe(Key(0x11, false, afterControl));
        }
        if (terminalShift) { Assert.Null(result); result = tracker.Observe(Key(0x10, false)); }
        Assert.Equal(new CopySelectionRequest(100, 42, 10), result);
        Assert.False(tracker.HasPendingCopy); Assert.Null(tracker.Observe(Key(0x11, false)));
    }

    [Theory]
    [InlineData("no-gesture")]
    [InlineData("expired")]
    [InlineData("window")]
    [InlineData("injected")]
    [InlineData("no-baseline")]
    [InlineData("alt")]
    [InlineData("reset")]
    public void Unrelated_copy_or_a_stale_gesture_is_not_authorization_to_read_the_clipboard(string scenario)
    {
        long time = 1000;
        var tracker = new CopySelectionIntentTracker(() => time);
        if (scenario != "no-gesture") tracker.Observe(Gesture());
        if (scenario == "expired") time += 16000;
        if (scenario == "reset") tracker.Reset();
        var key = Key(0x43, true, SelectionActivityModifiers.Control, true);
        if (scenario == "window") key = key with { ForegroundWindow = 200 };
        if (scenario == "injected") key = key with { IsInjected = true };
        if (scenario == "no-baseline") key = key with { HasCopySequence = false };
        if (scenario == "alt") key = key with { Modifiers = SelectionActivityModifiers.Control | SelectionActivityModifiers.Alt };
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
        Assert.False(tracker.HasPendingCopy);
    }

    [Fact]
    public void A_missing_copy_key_release_expires_instead_of_disabling_automatic_reads_forever()
    {
        long time = 1000;
        var tracker = new CopySelectionIntentTracker(() => time);
        tracker.Observe(Gesture()); tracker.Observe(Key(0x43, true, SelectionActivityModifiers.Control, true));
        Assert.True(tracker.HasPendingCopy);
        time += 16000;
        Assert.False(tracker.HasPendingCopy);
        Assert.Null(tracker.Observe(Key(0x43, false)));
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("own-writer")]
    [InlineData("foreign-owner")]
    [InlineData("password")]
    [InlineData("other-window")]
    public async Task Old_clipboard_program_copies_and_wrong_scopes_never_read_text(string scenario)
    {
        var api = new FakeApi();
        api.Metadata = scenario switch
        {
            "unchanged" => api.Metadata with { Sequence = 10 },
            "own-writer" => api.Metadata with { OwnerProcessId = api.OwnProcessId },
            "foreign-owner" => api.Metadata with { OwnerProcessId = 99 },
            "password" => api.Metadata with { ProtectedFocus = true },
            _ => api.Metadata with { ForegroundWindow = 200 }
        };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.NotEqual(CopySelectionStatus.Success, result.Status); Assert.Null(result.Selection); Assert.Equal(0, api.TextReads);
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
    [InlineData("sequence")]
    [InlineData("window")]
    [InlineData("cancelled")]
    public async Task Changes_while_reading_drop_the_text(string change)
    {
        bool current = true;
        var api = new FakeApi();
        api.BeforeRead = () =>
        {
            if (change == "sequence") api.Metadata = api.Metadata with { Sequence = 12 };
            if (change == "window") api.Metadata = api.Metadata with { ForegroundWindow = 200 };
            if (change == "cancelled") current = false;
        };
        using var reader = Reader(api);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => current, CancellationToken.None);
        Assert.Null(result.Selection); Assert.NotEqual(CopySelectionStatus.Success, result.Status);
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
    public async Task A_blocked_fake_renderer_times_out_and_cannot_accumulate_another_read()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var api = new FakeApi { BeforeRead = () => { entered.Set(); release.Wait(); } };
        using var reader = new WindowsCopiedSelectionReader(api, TimeSpan.FromMilliseconds(200), (_, _) => false);
        try
        {
            var first = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(CopySelectionStatus.TimedOut, (await first.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            Assert.Equal(CopySelectionStatus.Busy, (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
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
