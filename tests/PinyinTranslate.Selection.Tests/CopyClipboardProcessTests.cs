using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using PinyinTranslate.Selection;

namespace PinyinTranslate.Selection.Tests;

// Fake children and in-memory pipes only. No process or native clipboard is used.
public sealed class CopyClipboardProcessTests
{
    [Fact]
    public async Task A_timeout_keeps_the_worker_token_alive_until_helper_cleanup_finishes()
    {
        var capture = new DelayedCleanupCapture();
        using var reader = new WindowsCopiedSelectionReader(new MetadataApi(), TimeSpan.FromMilliseconds(200), capture: capture);
        try
        {
            var first = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
            await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CopySelectionStatus.TimedOut, (await first).Status);
            await capture.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(Record.Exception(() => _ = capture.Token.WaitHandle));
        }
        finally { capture.FinishCleanup.TrySetResult(); }
        Assert.Equal(CopySelectionStatus.Success,
            (await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_timed_out_renderer_is_disposed_and_the_next_copy_succeeds_instead_of_reporting_busy()
    {
        var output = new BlockedOutput(ignoreCancellation: true); var blocked = new FakeProcess(output);
        var next = await Reply(new(CopySelectionStatus.Success, 11, "IDEA documentation 😀"));
        var children = new Queue<FakeProcess>([blocked, next]);
        using var reader = new WindowsCopiedSelectionReader(new MetadataApi(), TimeSpan.FromMilliseconds(300),
            capture: new CopyClipboardProcessCapture(() => children.Dequeue()));
        var first = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CopySelectionStatus.TimedOut, (await first).Status);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Success, result.Status);
        Assert.Equal("IDEA documentation 😀", result.Selection!.Text);
        await blocked.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(next.Disposed.Task.IsCompleted); Assert.Empty(children);
    }

    [Fact]
    public async Task Cancelling_a_superseded_copy_closes_its_worker_and_allows_the_next_result()
    {
        var output = new BlockedOutput(); var blocked = new FakeProcess(output);
        var next = await Reply(new(CopySelectionStatus.Success, 11, "latest copy"));
        var children = new Queue<FakeProcess>([blocked, next]);
        using var reader = new WindowsCopiedSelectionReader(new MetadataApi(), capture:
            new CopyClipboardProcessCapture(() => children.Dequeue()));
        using var cancellation = new CancellationTokenSource();
        var first = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, cancellation.Token);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var second = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Cancelled, (await first).Status);
        Assert.Equal("latest copy", (await second).Selection!.Text);
        await blocked.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Disposing_the_reader_cancels_a_worker_that_is_waiting_for_text()
    {
        var output = new BlockedOutput(); var child = new FakeProcess(output);
        var reader = new WindowsCopiedSelectionReader(new MetadataApi(), capture: new CopyClipboardProcessCapture(() => child));
        var pending = reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        reader.Dispose();
        Assert.Equal(CopySelectionStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        await child.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Copy_scope_and_unicode_text_round_trip_over_bounded_memory_pipes()
    {
        var child = await Reply(new(CopySelectionStatus.Success, 11, "说明\n复制文本\t😀"));
        var capture = new CopyClipboardProcessCapture(() => child);
        var result = await capture.ReadAsync(new(100, 42, 10), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("说明\n复制文本\t😀", result.Selection!.Text);
        child.Request.Position = 0;
        var request = await CopyClipboardProtocol.ReadAsync<CopyClipboardWorkerRequest>(child.Request, 1024, CancellationToken.None);
        Assert.Equal(100L, request.Window); Assert.Equal(42, request.ProcessId); Assert.Equal(10u, request.BeforeSequence);
        Assert.Equal(5000, request.TimeoutMilliseconds); Assert.True(child.Disposed.Task.IsCompleted);
    }

    [Theory]
    [InlineData(0u, "text")]
    [InlineData(10u, "old text")]
    [InlineData(11u, "")]
    public async Task Missing_or_unchanged_text_in_a_worker_response_cannot_be_published(uint sequence, string text)
    {
        var child = await Reply(new(CopySelectionStatus.Success, sequence, text));
        var result = await new CopyClipboardProcessCapture(() => child).ReadAsync(new(100, 42, 10), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(CopySelectionStatus.NoNewText, result.Status); Assert.Null(result.Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Results_are_rechecked_against_parent_scope_and_own_clipboard_write_sequences(bool ownWrite)
    {
        var child = await Reply(new(CopySelectionStatus.Success, 11, "do not publish")); var api = new MetadataApi();
        var capture = new CallbackCapture(new CopyClipboardProcessCapture(() => child), () =>
        { if (!ownWrite) api.Metadata = api.Metadata with { ForegroundWindow = 200 }; });
        using var reader = new WindowsCopiedSelectionReader(api, ignoreOwnSequence: sequence => ownWrite && sequence == 11, capture: capture);
        var result = await reader.ReadAfterCopyAsync(new(100, 42, 10), () => true, CancellationToken.None);
        Assert.NotEqual(CopySelectionStatus.Success, result.Status); Assert.Null(result.Selection);
    }

    [Fact]
    public async Task An_oversized_pipe_frame_is_rejected_and_its_worker_is_disposed()
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, CopyClipboardProtocol.MaximumPayload + 1);
        var child = new FakeProcess(new MemoryStream(header));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CopyClipboardProcessCapture(() => child)
            .ReadAsync(new(100, 42, 10), TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(child.Disposed.Task.IsCompleted);
    }

    [Fact]
    public async Task A_failed_worker_exit_is_not_accepted_as_successful_text()
    {
        var child = await Reply(new(CopySelectionStatus.Success, 11, "ignore")); child.ExitCode = 5;
        var result = await new CopyClipboardProcessCapture(() => child).ReadAsync(new(100, 42, 10), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(CopySelectionStatus.Unavailable, result.Status); Assert.Null(result.Selection);
    }

    [Fact]
    public void A_framework_host_passes_paths_with_spaces_as_arguments_and_starts_no_visible_window()
    {
        var info = CopyClipboardProcessCapture.CreateStartInfo(@"C:\Program Files\dotnet\dotnet.exe", @"C:\My Tools\PinyinTranslate.App.dll", "PinyinTranslate.App");
        Assert.Equal(new[] { @"C:\My Tools\PinyinTranslate.App.dll", CopyClipboardWorker.Argument }, info.ArgumentList);
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow); Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.RedirectStandardInput); Assert.True(info.RedirectStandardOutput);
    }

    private static async Task<FakeProcess> Reply(CopyClipboardWorkerReply reply)
    {
        var output = new MemoryStream(); await CopyClipboardProtocol.WriteAsync(output, reply, CancellationToken.None); output.Position = 0;
        return new(output);
    }
    private sealed class MetadataApi : ICopyClipboardReadApi
    {
        public int OwnProcessId => 7;
        public CopyClipboardMetadata Metadata = new(100, 42, 11, 42);
        public CopyClipboardMetadata GetMetadata() => Metadata;
        public CopyClipboardText? TryReadText(int maximumLength) => throw new InvalidOperationException("Parent must not read native text.");
    }
    private sealed class DelayedCleanupCapture : ICopyClipboardCapture
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        private int calls;
        public async Task<CopySelectionResult> ReadAsync(CopySelectionRequest request, TimeSpan timeout, CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Token = cancellation; Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                finally { CleanupEntered.TrySetResult(); await FinishCleanup.Task; }
            }
            return new(CopySelectionStatus.Success, new(request.Window, request.ProcessId, 11, "next copy"));
        }
    }
    private sealed class CallbackCapture(ICopyClipboardCapture inner, Action after) : ICopyClipboardCapture
    {
        public async Task<CopySelectionResult> ReadAsync(CopySelectionRequest request, TimeSpan timeout, CancellationToken cancellation)
        { var result = await inner.ReadAsync(request, timeout, cancellation); after(); return result; }
    }
    private sealed class FakeProcess(Stream output) : ICopyClipboardProcess
    {
        public MemoryStream Request { get; } = new();
        public Stream Input => Request;
        public Stream Output => output;
        public int ExitCode { get; set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForExitAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void Terminate() { if (output is BlockedOutput blocked) blocked.Terminate.TrySetResult(); }
        public void Dispose() { Terminate(); Disposed.TrySetResult(); }
    }
    private sealed class BlockedOutput(bool ignoreCancellation = false) : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Terminate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (ignoreCancellation) await Terminate.Task;
            else await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
