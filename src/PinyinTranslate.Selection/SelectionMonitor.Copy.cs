namespace PinyinTranslate.Selection;

public sealed partial class SelectionMonitor
{
    private readonly ISelectionClipboardReader? copyReader;
    private readonly CopySelectionIntentTracker copyIntent;
    private CancellationTokenSource? copyCapture;
    private sealed record CopyWork(CopySelectionRequest Request, long Epoch, long Version, long Revision,
        CancellationTokenSource Cancellation, CancellationToken Token);

    private async Task ReadyForCopiedSelectionAsync()
    {
        try
        {
            if (activity is not null && await activity.Ready.ConfigureAwait(false))
            {
                lock (gate) if (disposed) return;
                ready.TrySetResult(true);
            }
        }
        catch { /* A working UIA path can still report readiness independently. */ }
    }
    private bool CopyIsCurrent(CopiedSelection copy)
    {
        try { return copyReader?.IsCurrent(copy) == true; } catch { return false; }
    }
    private void CancelCopyCaptureLocked()
    {
        var cancellation = copyCapture; copyCapture = null;
        if (cancellation is null) return;
        try { _ = IgnoreCopyCancellationAsync(cancellation.CancelAsync()); } catch (ObjectDisposedException) { }
    }
    private static async Task IgnoreCopyCancellationAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private bool CopyWorkCurrentLocked(CopyWork work) => !disposed && enabled && epoch == work.Epoch && version == work.Version &&
        ReferenceEquals(copyCapture, work.Cancellation) && !work.Token.IsCancellationRequested && !gestureInProgress &&
        handledActivityRevision == work.Revision && (activity?.Revision ?? 0) == work.Revision;

    private async Task ReadCopiedSelectionAsync(CopyWork work)
    {
        bool Current() { lock (gate) return CopyWorkCurrentLocked(work); }
        Task<CopySelectionResult>? captureTask = null;
        try
        {
            if (copyReader is null || !Current()) return;
            lock (gate) CopyStatusLocked(work, "正在读取复制文字…");
            captureTask = copyReader.ReadAfterCopyAsync(work.Request, Current, work.Token);
            var result = await captureTask.WaitAsync(WindowsCopiedSelectionReader.DefaultTimeout + TimeSpan.FromSeconds(1), work.Token)
                .ConfigureAwait(false);
            lock (gate)
            {
                if (!CopyWorkCurrentLocked(work)) return;
                copyCapture = null;
                var copied = result.Selection;
                if (result.Status == CopySelectionStatus.Success && copied is not null && copied.Window == work.Request.Window &&
                    copied.ProcessId == work.Request.ProcessId && copied.Sequence != 0 &&
                    (copied.Sequence != work.Request.BeforeSequence || copied.SourceOwnedBaseline) &&
                    !string.IsNullOrWhiteSpace(copied.Text) && copied.Text.Length <= options.MaximumTextLength)
                {
                    string runtime = $"copied:{copied.ProcessId}:{copied.Window.ToInt64():X}";
                    string scope = copied.Window.ToInt64().ToString("X");
                    string range = "copy:" + copied.Sequence;
                    var evidence = new SelectionReadResult(copied.ProcessId, unchecked((int)copied.Window.ToInt64()), runtime, copied.Text, range, scope);
                    var snapshot = new SelectionSnapshot(version, copied.ProcessId, evidence.WindowHandle, runtime, copied.Text)
                    { RangeId = range, ScopeId = scope, ActivityRevision = work.Revision, IsCopiedText = true };
                    freshBlocked = freshActivity = completedGestureRead = emptyGestureNotified = false;
                    current = new(evidence, snapshot, copied);
                    Notice(() => { if (IsCurrent(snapshot.Version)) Invoke(SelectionChanged, snapshot); });
                }
                else
                {
                    string message = result.Status switch
                    {
                        CopySelectionStatus.TooLong => "复制内容过长，本次未翻译；正常复制不受影响，可在设置中查看自动翻译上限。",
                        CopySelectionStatus.Busy or CopySelectionStatus.TimedOut => "复制读取暂未完成，再按 Ctrl+C 可重试。",
                        _ => "等待复制文字，选中文字后按 Ctrl+C 翻译。"
                    };
                    CopyStatusLocked(work, message);
                }
            }
        }
        catch (OperationCanceledException) when (work.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Trace("copy-read=" + error.GetType().Name);
            lock (gate)
            {
                if (CopyWorkCurrentLocked(work))
                { copyCapture = null; CopyStatusLocked(work, "本次复制读取未完成，可再次按 Ctrl+C 重试。"); }
            }
        }
        finally
        {
            lock (gate) if (ReferenceEquals(copyCapture, work.Cancellation)) copyCapture = null;
            _ = FinishCopyCaptureAsync(work.Cancellation, captureTask);
            ScheduleRead();
        }
    }
    private static async Task FinishCopyCaptureAsync(CancellationTokenSource cancellation, Task? captureTask)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); } catch { }
        if (captureTask is not null)
            try { await captureTask.ConfigureAwait(false); } catch { }
        cancellation.Dispose();
    }
    private void CopyStatusLocked(CopyWork work, string message) => Notice(() =>
    {
        lock (gate) if (disposed || !enabled || epoch != work.Epoch || version != work.Version || current is not null) return;
        Invoke(StatusChanged, message);
    });
}
