using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// The real reader and message sequence run against synthetic metadata/strings.
// No native API, UIA, window, clipboard, input hook or real process is used.
public sealed class NativeSelectionReaderTests
{
    [Fact]
    public void ConstructionDoesNotReadWindowMetadataOrSendMessages()
    {
        var api = new FakeApi();
        _ = Reader(api);
        Assert.Equal(0, api.InfoCalls); Assert.Empty(api.Messages);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("RichEdit20W")]
    [InlineData("RICHEDIT50W")]
    [InlineData("RichEditD2DPT")]
    public void StableRealSelectionIsReturnedWithoutChangingTheTarget(string className)
    {
        var api = new FakeApi();
        api.Info = api.Info with { ClassName = className, Style = 0x800 }; // Read-only text is eligible.
        var result = Reader(api).Read(10, () => true);
        Assert.NotNull(result); Assert.Equal("world", result.Text);
        Assert.Equal((nint)10, result.WindowHandle); Assert.Equal(42, result.ProcessId);
        Assert.Equal(6, result.Start); Assert.Equal(11, result.End);
        Assert.Equal(64, result.DocumentFingerprint.Length);
        Assert.Equal(2, api.TextCalls); Assert.Equal("Hello world", api.Text);
        Assert.All(api.Messages, value => Assert.Contains(value, new[] { "selection", "length", "text" }));
    }

    [Fact]
    public void MetadataScopeRejectsPasswordOwnProcessUnknownClassAnsiAndBackgroundBeforeReadingText()
    {
        var valid = new FakeApi().Info;
        NativeSelectionWindowInfo[] denied =
        [
            valid with { Style = 0x20 }, valid with { ProcessId = 7, ForegroundProcessId = 7 },
            valid with { ClassName = "Static" }, valid with { ClassName = "EditUnverifiedSuffix" },
            valid with { IsUnicode = false }, valid with { ForegroundRootWindow = 200 },
            valid with { ForegroundProcessId = 99 }, valid with { ForegroundWindow = 0 }, valid with { ProcessId = 0 }
        ];
        foreach (var metadata in denied)
        {
            var api = new FakeApi { Info = metadata };
            Assert.Null(Reader(api).Read(10, () => true));
            Assert.Empty(api.Messages);
        }
    }

    [Fact]
    public void CallerScopeAndCancellationAreCheckedBeforeEveryNativeMessage()
    {
        var api = new FakeApi();
        bool allowed = true;
        api.OnMessage = message => { if (message == "text") allowed = false; };
        Assert.Null(Reader(api).Read(10, () => allowed));
        Assert.Equal(1, api.TextCalls); Assert.Equal("text", api.Messages[^1]);
        using var canceled = new CancellationTokenSource();
        var second = new FakeApi { OnMessage = _ => canceled.Cancel() };
        Assert.Null(Reader(second).Read(10, () => true, canceled.Token));
        Assert.Single(second.Messages);
    }

    [Fact]
    public void ForegroundPasswordOrHandleIdentityChangesDuringAReadDiscardAllText()
    {
        foreach (var change in new Func<NativeSelectionWindowInfo, NativeSelectionWindowInfo>[]
        {
            info => info with { ForegroundWindow = 200, ForegroundRootWindow = 200 },
            info => info with { Style = 0x20 }, info => info with { ProcessId = 99 }
        })
        {
            var api = new FakeApi();
            api.OnMessage = message => { if (message == "text") api.Info = change(api.Info); };
            Assert.Null(Reader(api).Read(10, () => true));
            Assert.Equal(1, api.TextCalls);
        }
    }

    [Theory]
    [InlineData(0xffffffffu)] // packed -1, including indices beyond 65535
    [InlineData(0x0006000bu)] // reversed endpoints
    [InlineData(0x00060006u)] // collapsed caret, no selection
    [InlineData(0x00140006u)] // selection extends past the actual document
    public void InvalidPackedOrOutOfDocumentRangesNeverBecomeASelection(uint packed)
    {
        var api = new FakeApi { PackedOverride = packed };
        Assert.Null(Reader(api).Read(10, () => true));
    }

    [Fact]
    public void OversizedSelectionAndOversizedDocumentAreRejectedBeforeBufferAllocation()
    {
        var selection = new FakeApi { Text = new string('x', 4001), Start = 0, End = 4001 };
        Assert.Null(Reader(selection).Read(10, () => true)); Assert.Equal(0, selection.TextCalls);
        var document = new FakeApi { LengthOverride = 65536 };
        Assert.Null(Reader(document).Read(10, () => true)); Assert.Equal(0, document.TextCalls);
    }

    [Fact]
    public void EndAt65535IsValidButTheOverflowSentinelIsNeverTruncated()
    {
        var api = new FakeApi { Text = new string('x', 65535), Start = 65534, End = 65535 };
        var result = Reader(api).Read(10, () => true);
        Assert.NotNull(result); Assert.Equal("x", result.Text); Assert.Equal(65535, result.End);
        api.PackedOverride = uint.MaxValue;
        Assert.Null(Reader(api).Read(10, () => true));
    }

    [Fact]
    public void LengthUpperBoundMayOverestimateButCopiedCountMustMatchTheActualText()
    {
        var api = new FakeApi { LengthOverride = 20 };
        Assert.Equal("world", Reader(api).Read(10, () => true)!.Text);
        api.CopiedOverride = 12;
        Assert.Null(Reader(api).Read(10, () => true));
    }

    [Fact]
    public void WholeDocumentAndRangeMustBeStableAcrossBothCaptures()
    {
        var body = new FakeApi();
        body.OnMessage = message => { if (message == "text" && body.TextCalls == 2) body.Text = "Jello world"; };
        Assert.Null(Reader(body).Read(10, () => true));
        var range = new FakeApi { Text = "word word", Start = 0, End = 4 };
        int selections = 0;
        range.OnMessage = message => { if (message == "selection" && ++selections == 3) { range.Start = 5; range.End = 9; } };
        Assert.Null(Reader(range).Read(10, () => true)); // Same selected text, different location.
        var during = new FakeApi { Text = "word word", Start = 0, End = 4 };
        during.OnMessage = message => { if (message == "text") { during.Start = 5; during.End = 9; } };
        Assert.Null(Reader(during).Read(10, () => true));
    }

    [Fact]
    public void RevalidationIdentityIncludesPositionAndUnselectedDocumentFingerprint()
    {
        var api = new FakeApi { Text = "word word", Start = 0, End = 4 };
        var first = Reader(api).Read(10, () => true)!;
        api.Start = 5; api.End = 9;
        var otherPosition = Reader(api).Read(10, () => true)!;
        Assert.Equal(first.Text, otherPosition.Text); Assert.NotEqual(first, otherPosition);
        api.Start = 0; api.End = 4; api.Text = "word next";
        var otherBody = Reader(api).Read(10, () => true)!;
        Assert.Equal(first.Text, otherBody.Text); Assert.NotEqual(first.DocumentFingerprint, otherBody.DocumentFingerprint);
    }

    [Fact]
    public void TimeoutsExceptionsAndLateReturnsDoNotPublishOrStartFurtherReads()
    {
        foreach (string failed in new[] { "selection", "length", "text" })
        {
            var api = new FakeApi { FailedMessage = failed };
            Assert.Null(Reader(api).Read(10, () => true)); Assert.Equal(failed, api.Messages[^1]);
        }
        var late = new FakeApi();
        late.OnMessage = _ => late.Clock += 700;
        Assert.Null(Reader(late).Read(10, () => true)); Assert.Single(late.Messages);
        Assert.All(late.Timeouts, value => Assert.InRange(value, 1, 50));
        var broken = new FakeApi { OnMessage = _ => throw new InvalidOperationException() };
        Assert.Null(Reader(broken).Read(10, () => true));
        Assert.Null(Reader(new()).Read(10, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void CallerAndMessageCallbacksStayOnTheReadingThread()
    {
        var api = new FakeApi();
        int caller = Environment.CurrentManagedThreadId;
        api.OnMessage = _ => Assert.Equal(caller, Environment.CurrentManagedThreadId);
        Assert.NotNull(Reader(api).Read(10, () => { Assert.Equal(caller, Environment.CurrentManagedThreadId); return true; }));
    }

    [Fact]
    public void AmbiguousRichEditCrLfAndEditSoftBreakOffsetsAreRejectedWithoutNormalization()
    {
        var rich = new FakeApi { Text = "a\r\nword", Start = 3, End = 7 };
        rich.Info = rich.Info with { ClassName = "RICHEDIT50W" };
        Assert.Null(Reader(rich).Read(10, () => true));
        var edit = new FakeApi { Text = "a\r\r\nword", Start = 4, End = 8 };
        Assert.Null(Reader(edit).Read(10, () => true));
        var hardBreak = new FakeApi { Text = "a\r\nword", Start = 3, End = 7 };
        Assert.Equal("word", Reader(hardBreak).Read(10, () => true)!.Text);
    }

    [Fact]
    public void SurrogatesCannotBeSplitAndBlankSelectionsAreNotPublished()
    {
        var api = new FakeApi { Text = "😀word", Start = 1, End = 2 };
        Assert.Null(Reader(api).Read(10, () => true));
        api.Start = 0; api.End = 2;
        Assert.Equal("😀", Reader(api).Read(10, () => true)!.Text);
        api.Text = "   "; api.Start = 0; api.End = 3;
        Assert.Null(Reader(api).Read(10, () => true));
    }

    private static NativeSelectionReader Reader(FakeApi api) => new(api, ownProcessId: () => 7, clockMilliseconds: () => api.Clock);

    private sealed class FakeApi : INativeSelectionReadApi
    {
        public NativeSelectionWindowInfo Info = new(10, 42, 100, 100, 100, 42, "Edit", 0, true);
        public string Text = "Hello world";
        public int Start = 6, End = 11, InfoCalls, TextCalls;
        public int? LengthOverride, CopiedOverride;
        public uint? PackedOverride;
        public long Clock;
        public string? FailedMessage;
        public Action<string>? OnMessage;
        public List<string> Messages { get; } = [];
        public List<int> Timeouts { get; } = [];
        private bool Message(string message, int timeout)
        {
            Messages.Add(message); Timeouts.Add(timeout);
            if (message == "text") TextCalls++;
            OnMessage?.Invoke(message);
            return FailedMessage != message;
        }
        public bool TryGetWindowInfo(nint window, out NativeSelectionWindowInfo info) { InfoCalls++; info = Info; return true; }
        public bool TryGetTextLength(nint window, int timeoutMilliseconds, out int length)
        { bool success = Message("length", timeoutMilliseconds); length = LengthOverride ?? Text.Length; return success; }
        public bool TryGetText(nint window, int capacity, int timeoutMilliseconds, out string text, out int copiedCharacters)
        {
            bool success = Message("text", timeoutMilliseconds);
            text = Text[..Math.Min(Text.Length, capacity - 1)]; copiedCharacters = CopiedOverride ?? text.Length;
            return success;
        }
        public bool TryGetPackedSelection(nint window, int timeoutMilliseconds, out uint packedRange)
        {
            bool success = Message("selection", timeoutMilliseconds);
            packedRange = PackedOverride ?? ((uint)End << 16 | (uint)Start);
            return success;
        }
    }
}
