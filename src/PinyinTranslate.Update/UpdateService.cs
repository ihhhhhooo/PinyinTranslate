using System.Net;
using System.Threading.Channels;

namespace PinyinTranslate.Update;

/// <summary>UI-free state machine. Only the backend owns downloads and updater process handoff.</summary>
public sealed class UpdateService : IDisposable
{
    private readonly object gate = new();
    private readonly IUpdateBackend backend;
    private readonly IUpdateLifecycle lifecycle;
    private readonly Channel<UpdateState> notices = Channel.CreateUnbounded<UpdateState>(new()
        { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly bool managed;
    private UpdateState state;
    private Operation? operation;
    private bool disposed;
    private long operationSequence;
    private sealed class Operation(long id, UpdatePhase phase, CancellationToken token)
    {
        public long Id { get; } = id;
        public UpdatePhase Phase { get; } = phase;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HandoffStarted;
    }
    public event Action<UpdateState>? StateChanged;
    public UpdateState State { get { lock (gate) return state; } }

    public UpdateService(IUpdateBackend backend, IUpdateLifecycle lifecycle)
    {
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        managed = backend.IsManaged;
        state = new(0, managed ? UpdatePhase.Idle : UpdatePhase.MigrationRequired, backend.CurrentVersion, null, 0,
            managed ? "点击检查更新。" : MigrationMessage);
        _ = Task.Run(DrainNoticesAsync);
    }

    public const string MigrationMessage = "当前是普通解压版，尚不能自动更新。请从项目发布页下载安装版，或下载新版便携包并解压一次；现有设置会保留。";
    public Task CheckAsync(CancellationToken cancellationToken = default) => Start(UpdatePhase.Checking, cancellationToken);
    public Task DownloadAsync(CancellationToken cancellationToken = default) => Start(UpdatePhase.Downloading, cancellationToken);
    public Task InstallAsync(CancellationToken cancellationToken = default) => Start(UpdatePhase.PreparingRestart, cancellationToken);

    private Task Start(UpdatePhase phase, CancellationToken token)
    {
        Operation run;
        UpdateRelease? release;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            if (operation is not null) return operation.Completion.Task;
            if (!managed) return Task.CompletedTask;
            bool allowed = phase switch
            {
                UpdatePhase.Checking => state.CanCheck,
                UpdatePhase.Downloading => state.CanDownload,
                _ => state.CanInstall
            };
            if (!allowed) return Task.CompletedTask;
            release = phase == UpdatePhase.Checking ? null : state.Release;
            run = new(++operationSequence, phase, token);
            operation = run;
            SetStateLocked(phase, release, 0, phase switch
            {
                UpdatePhase.Checking => "正在检查更新…",
                UpdatePhase.Downloading => "正在连接更新源，准备下载…",
                _ => "正在保存状态并暂停翻译服务…"
            });
        }
        _ = RunAsync(run, release);
        return run.Completion.Task;
    }

    private async Task RunAsync(Operation run, UpdateRelease? release)
    {
        bool preparationAttempted = false;
        bool handoffSucceeded = false;
        try
        {
            var token = run.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            if (run.Phase == UpdatePhase.Checking)
            {
                var found = await backend.CheckAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!IsCurrentLocked(run)) return;
                    token.ThrowIfCancellationRequested();
                    SetStateLocked(found is null ? UpdatePhase.UpToDate : UpdatePhase.Available, found, 0,
                        found is null ? "当前已是最新版本。" : $"发现新版本 {found.Version}，可下载更新。");
                }
            }
            else if (run.Phase == UpdatePhase.Downloading)
            {
                await backend.DownloadAsync(release!, new InlineProgress(value => ReportProgress(run, value)), token).ConfigureAwait(false);
                lock (gate)
                {
                    if (!IsCurrentLocked(run)) return;
                    token.ThrowIfCancellationRequested();
                    SetStateLocked(UpdatePhase.ReadyToInstall, release, 100, "更新已下载并校验，点击安装并重启。");
                }
            }
            else
            {
                preparationAttempted = true;
                await lifecycle.PrepareAsync(token).ConfigureAwait(false);
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrentLocked(run)) throw new OperationCanceledException(token);
                    // Once a synchronous updater handoff starts it cannot safely
                    // be revoked; cancellation is checked immediately before it.
                    run.HandoffStarted = true;
                    // Serialize the final close/dispose check with the synchronous
                    // handoff. Never hold this lock across Dispatcher lifecycle calls.
                    try { backend.PrepareRestart(release!); }
                    catch { run.HandoffStarted = false; throw; }
                    handoffSucceeded = true;
                    preparationAttempted = false;
                    if (!disposed) SetStateLocked(UpdatePhase.RestartScheduled, release, 100, "更新已交给更新程序，正在退出并重启…");
                }
                bool exit;
                lock (gate) exit = !disposed;
                if (exit) lifecycle.Exit();
            }
        }
        catch (Exception error)
        {
            bool resumeFailed = false;
            bool resume;
            lock (gate) resume = preparationAttempted && !disposed;
            if (resume)
            {
                // PrepareRestart can throw before starting the updater. Keep the
                // current app open and resume its reversible preparation.
                try { await lifecycle.ResumeAsync().ConfigureAwait(false); } catch { resumeFailed = true; }
            }
            lock (gate)
            {
                if (IsCurrentLocked(run))
                {
                    bool cancelled = error is OperationCanceledException;
                    SetStateLocked(handoffSucceeded ? UpdatePhase.RestartScheduled : cancelled ? UpdatePhase.Cancelled : UpdatePhase.Failed,
                        run.Phase == UpdatePhase.Checking ? null : release, 0,
                        handoffSucceeded ? "更新程序已就绪，请关闭当前软件以继续更新。" :
                        resumeFailed ? "更新未完成，翻译服务恢复失败；可关闭后重新打开软件。" :
                        cancelled ? "已取消更新操作。" : FailureMessage(error, run.Phase));
                }
            }
        }
        finally
        {
            lock (gate) if (ReferenceEquals(operation, run)) operation = null;
            run.Cancellation.Dispose();
            run.Completion.TrySetResult();
        }
    }

    private bool IsCurrentLocked(Operation run) => !disposed && ReferenceEquals(operation, run);
    private void ReportProgress(Operation run, int value)
    {
        lock (gate)
        {
            if (!IsCurrentLocked(run) || run.Cancellation.IsCancellationRequested || state.Phase != UpdatePhase.Downloading) return;
            int progress = Math.Max(state.Progress, Math.Clamp(value, 0, 100));
            if (progress != state.Progress)
            {
                string message = progress >= 100 ? "下载已完成，正在校验更新文件…" : state.Release is { DownloadBytes: > 0 } release
                    ? $"正在下载更新… {progress}%（约 {release.DownloadBytes / 1048576d * progress / 100:0.0} / {release.DownloadBytes / 1048576d:0.0} MB）"
                    : $"正在下载更新… {progress}%";
                SetStateLocked(UpdatePhase.Downloading, state.Release, progress, message);
            }
        }
    }
    private void SetStateLocked(UpdatePhase phase, UpdateRelease? release, int progress, string message)
    {
        state = new(state.Revision + 1, phase, state.CurrentVersion, release, progress, message);
        notices.Writer.TryWrite(state);
    }
    private async Task DrainNoticesAsync()
    {
        await foreach (var notice in notices.Reader.ReadAllAsync())
        {
            var handlers = StateChanged;
            if (handlers is null) continue;
            foreach (Action<UpdateState> handler in handlers.GetInvocationList())
            {
                lock (gate) if (disposed || state.Revision != notice.Revision) break;
                try { handler(notice); } catch { }
            }
        }
    }
    private static string FailureMessage(Exception error, UpdatePhase phase) => error switch
    {
        UpdateFeedUnavailableException => "尚未发布可自动更新的版本，请稍后重试。",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "无法读取更新；GitHub 访问受限或请求过于频繁，请稍后重试。",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "尚未找到可用的更新发布，请稍后重试。",
        InvalidDataException => "更新文件或发布信息未通过校验，未执行安装。",
        HttpRequestException { InnerException: TimeoutException } => "更新下载长时间未收到新数据，请检查网络后重试。",
        HttpRequestException => "连接更新服务失败，请检查网络后重试。",
        TimeoutException => "更新请求超时，请稍后重试。",
        _ when phase == UpdatePhase.PreparingRestart => "无法启动更新程序，当前软件仍保持打开，请重试。",
        _ => "更新操作未完成，请稍后重试。"
    };
    public void Cancel()
    {
        Operation? cancelled;
        lock (gate)
        {
            cancelled = operation;
            if (disposed || cancelled is null || cancelled.HandoffStarted || cancelled.Phase == UpdatePhase.PreparingRestart) return;
            operation = null;
            SetStateLocked(UpdatePhase.Cancelled, cancelled.Phase == UpdatePhase.Downloading ? state.Release : null, 0, "已取消更新操作。");
        }
        CancelWithoutCallbacksOnCaller(cancelled.Cancellation);
    }
    private static void CancelWithoutCallbacksOnCaller(CancellationTokenSource cancellation)
    {
        try { _ = ObserveCancellationAsync(cancellation); } catch (ObjectDisposedException) { }
    }
    private static async Task ObserveCancellationAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { } catch (AggregateException) { }
    }
    public void Dispose()
    {
        Operation? cancelled;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; cancelled = operation;
        }
        if (cancelled is not null && !cancelled.HandoffStarted) CancelWithoutCallbacksOnCaller(cancelled.Cancellation);
        notices.Writer.TryComplete();
    }
    private sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
}
