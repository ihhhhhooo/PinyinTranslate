using System.Collections.Concurrent;
using PinyinTranslate.Update;

namespace PinyinTranslate.Update.Tests;

// Every backend and lifecycle below is managed fake code. No locator, downloader,
// VelopackApp, updater process, application window or desktop service is created.
public sealed class UpdateServiceTests
{
    [Fact]
    public async Task OrdinaryZipOnlyOffersMigrationAndCallsNoUpdateOperations()
    {
        var backend = new Backend { IsManaged = false };
        var lifecycle = new Lifecycle();
        using var service = new UpdateService(backend, lifecycle);
        await service.CheckAsync(); await service.DownloadAsync(); await service.InstallAsync();
        Assert.Equal(UpdatePhase.MigrationRequired, service.State.Phase);
        Assert.Contains("解压一次", service.State.Message);
        Assert.False(service.State.CanInstall); Assert.False(service.State.CanDownload);
        Assert.Equal(0, backend.Checks + backend.Downloads + backend.Handoffs);
        Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task InstallRequiresSuccessfulDownloadAndOrdersPauseHandoffThenExit()
    {
        var calls = new ConcurrentQueue<string>();
        var backend = new Backend { Calls = calls };
        var lifecycle = new Lifecycle { Calls = calls };
        using var service = new UpdateService(backend, lifecycle);
        await service.InstallAsync();
        await service.CheckAsync();
        Assert.True(service.State.CanDownload); Assert.False(service.State.CanInstall);
        await service.InstallAsync();
        Assert.Equal(0, backend.Handoffs);
        await service.DownloadAsync();
        Assert.Equal(UpdatePhase.ReadyToInstall, service.State.Phase);
        await service.InstallAsync();
        Assert.Equal(UpdatePhase.RestartScheduled, service.State.Phase);
        Assert.Equal(new[] { "check", "download", "prepare", "handoff", "exit" }, calls.ToArray());
        await service.InstallAsync();
        Assert.Equal(1, backend.Handoffs); Assert.Equal(1, lifecycle.Exits);
    }

    [Fact]
    public async Task HandoffFailureResumesReversiblePreparationAndNeverExits()
    {
        var calls = new ConcurrentQueue<string>();
        var backend = new Backend { Calls = calls, HandoffError = new IOException("hidden path") };
        var lifecycle = new Lifecycle { Calls = calls };
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service);
        await service.InstallAsync();
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Equal(new[] { "check", "download", "prepare", "handoff", "resume" }, calls.ToArray());
        Assert.Equal(0, lifecycle.Exits);
        Assert.DoesNotContain("hidden path", service.State.Message);
    }

    [Fact]
    public async Task PreparationFailureResumesWithoutCallingTheUpdater()
    {
        var backend = new Backend();
        var lifecycle = new Lifecycle { Prepare = _ => Task.FromException(new InvalidOperationException("private detail")) };
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service); await service.InstallAsync();
        Assert.Equal(0, backend.Handoffs); Assert.Equal(0, lifecycle.Exits);
        Assert.Equal(new[] { "prepare", "resume" }, lifecycle.Calls.ToArray());
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
    }

    [Fact]
    public async Task DownloadOrHashFailureCannotReachLifecycleOrHandoff()
    {
        var backend = new Backend { Download = (_, _, _) => Task.FromException(new InvalidDataException("Checksum mismatch")) };
        var lifecycle = new Lifecycle();
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service); await service.InstallAsync();
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("校验", service.State.Message);
        Assert.False(service.State.CanInstall);
        Assert.Equal(0, backend.Handoffs); Assert.Empty(lifecycle.Calls);
    }

    [Fact]
    public async Task NoNewReleaseIsReportedAsUpToDateWithoutDownloading()
    {
        var backend = new Backend { Check = _ => Task.FromResult<UpdateRelease?>(null) };
        using var service = new UpdateService(backend, new Lifecycle());
        await service.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, service.State.Phase);
        Assert.Null(service.State.Release); Assert.Equal(0, backend.Downloads);
    }

    [Fact]
    public async Task MissingFeedIsNotMisreportedAsUpToDate()
    {
        var backend = new Backend { Check = _ => Task.FromException<UpdateRelease?>(new UpdateFeedUnavailableException()) };
        using var service = new UpdateService(backend, new Lifecycle());
        await service.CheckAsync();
        Assert.Equal(UpdatePhase.Failed, service.State.Phase);
        Assert.Contains("尚未发布", service.State.Message);
    }

    [Fact]
    public async Task SimultaneousClicksShareASingleCheckOperation()
    {
        var result = Signal<UpdateRelease?>();
        var backend = new Backend { Check = _ => result.Task };
        using var service = new UpdateService(backend, new Lifecycle());
        var first = service.CheckAsync();
        var second = service.CheckAsync();
        Assert.Same(first, second); Assert.Equal(1, backend.Checks);
        result.SetResult(new("1.0.1"));
        await Task.WhenAll(first, second);
        Assert.Equal(UpdatePhase.Available, service.State.Phase);
    }

    [Fact]
    public async Task CancelledLateCheckCannotOverwriteANewerSuccessfulCheck()
    {
        var late = Signal<UpdateRelease?>();
        var backend = new Backend { Check = _ => late.Task };
        using var service = new UpdateService(backend, new Lifecycle());
        var old = service.CheckAsync();
        service.Cancel();
        backend.Check = _ => Task.FromResult<UpdateRelease?>(new("1.0.2"));
        await service.CheckAsync();
        late.SetResult(new("1.0.1")); await old;
        Assert.Equal(UpdatePhase.Available, service.State.Phase);
        Assert.Equal("1.0.2", service.State.Release!.Version);
    }

    [Fact]
    public async Task CancelledLateFaultCannotOverwriteANewerRelease()
    {
        var late = Signal<UpdateRelease?>();
        var backend = new Backend { Check = _ => late.Task };
        using var service = new UpdateService(backend, new Lifecycle());
        var old = service.CheckAsync(); service.Cancel();
        backend.Check = _ => Task.FromResult<UpdateRelease?>(new("1.0.3"));
        await service.CheckAsync();
        late.SetException(new HttpRequestException("late")); await old;
        Assert.Equal("1.0.3", service.State.Release!.Version);
        Assert.Equal(UpdatePhase.Available, service.State.Phase);
    }

    [Fact]
    public async Task CancelledDownloadAndLateProgressCannotMakeAnOldReleaseInstallable()
    {
        var late = Signal();
        IProgress<int>? oldProgress = null;
        var backend = new Backend { Download = (_, progress, _) => { oldProgress = progress; return late.Task; } };
        using var service = new UpdateService(backend, new Lifecycle());
        await service.CheckAsync();
        var old = service.DownloadAsync(); service.Cancel();
        backend.Check = _ => Task.FromResult<UpdateRelease?>(new("1.0.2"));
        await service.CheckAsync();
        long revision = service.State.Revision;
        oldProgress!.Report(99); late.SetResult(); await old;
        Assert.Equal(revision, service.State.Revision);
        Assert.Equal("1.0.2", service.State.Release!.Version);
        Assert.False(service.State.CanInstall);
    }

    [Fact]
    public async Task ProgressIsClampedMonotonicAndIgnoredAfterCompletion()
    {
        var finished = Signal();
        IProgress<int>? progress = null;
        var backend = new Backend { Download = (_, captured, _) =>
        { progress = captured; return finished.Task; } };
        using var service = new UpdateService(backend, new Lifecycle());
        await service.CheckAsync();
        var download = service.DownloadAsync();
        progress!.Report(-10); Assert.Equal(0, service.State.Progress);
        progress.Report(70); Assert.Equal(70, service.State.Progress);
        progress.Report(20); Assert.Equal(70, service.State.Progress);
        progress.Report(150); Assert.Equal(100, service.State.Progress);
        Assert.False(service.State.CanInstall); // 100% transport progress is not verification success.
        finished.SetResult(); await download;
        Assert.Equal(100, service.State.Progress);
        long revision = service.State.Revision;
        progress!.Report(1000);
        Assert.Equal(revision, service.State.Revision);
    }

    [Fact]
    public async Task CancellationDuringPreparationRollsBackBeforeAnyHandoff()
    {
        var prepared = Signal();
        var backend = new Backend();
        var lifecycle = new Lifecycle { Prepare = _ => prepared.Task };
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service);
        using var cancel = new CancellationTokenSource();
        var install = service.InstallAsync(cancel.Token);
        cancel.Cancel(); prepared.SetResult(); await install;
        Assert.Equal(UpdatePhase.Cancelled, service.State.Phase);
        Assert.Equal(0, backend.Handoffs); Assert.Equal(0, lifecycle.Exits);
        Assert.Equal(new[] { "prepare", "resume" }, lifecycle.Calls.ToArray());
    }

    [Fact]
    public async Task PreparationIsSingleFlightAndCannotBeDetachedByTheCancelButton()
    {
        var prepared = Signal();
        var backend = new Backend();
        var lifecycle = new Lifecycle { Prepare = _ => prepared.Task };
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service);
        var first = service.InstallAsync(); service.Cancel();
        Assert.Same(first, service.InstallAsync());
        Assert.False(service.State.CanCancel);
        prepared.SetResult(); await first;
        Assert.Equal(1, backend.Handoffs); Assert.Equal(1, lifecycle.Exits);
    }

    [Fact]
    public async Task DisposeDuringPreparationDoesNotTouchClosingLifecycleOrExit()
    {
        var prepared = Signal();
        var backend = new Backend();
        var lifecycle = new Lifecycle { Prepare = _ => prepared.Task };
        var service = new UpdateService(backend, lifecycle);
        await Ready(service);
        var install = service.InstallAsync(); service.Dispose(); prepared.SetResult(); await install;
        Assert.Equal(0, backend.Handoffs); Assert.Equal(0, lifecycle.Exits);
        Assert.DoesNotContain("resume", lifecycle.Calls);
    }

    [Fact]
    public async Task SuccessfullyArmedUpdaterIsNotResumedIfApplicationExitThrows()
    {
        var backend = new Backend();
        var lifecycle = new Lifecycle { ExitError = new InvalidOperationException("exit failed") };
        using var service = new UpdateService(backend, lifecycle);
        await Ready(service); await service.InstallAsync();
        Assert.Equal(1, backend.Handoffs);
        Assert.Equal(UpdatePhase.RestartScheduled, service.State.Phase);
        Assert.DoesNotContain("resume", lifecycle.Calls);
        Assert.False(service.State.CanCheck); Assert.False(service.State.CanDownload);
    }

    [Fact]
    public async Task ClosingDuringSuccessfulHandoffDoesNotExitOrResumeClosingServices()
    {
        var backend = new Backend();
        var lifecycle = new Lifecycle();
        using var service = new UpdateService(backend, lifecycle);
        backend.OnHandoff = service.Dispose;
        await Ready(service); await service.InstallAsync();
        Assert.Equal(1, backend.Handoffs);
        Assert.Equal(0, lifecycle.Exits);
        Assert.DoesNotContain("resume", lifecycle.Calls);
    }

    [Fact]
    public async Task ClosingDuringFailedHandoffNeverResumesDisposedServices()
    {
        var backend = new Backend { HandoffError = new IOException("failed") };
        var lifecycle = new Lifecycle();
        using var service = new UpdateService(backend, lifecycle);
        backend.OnHandoff = service.Dispose;
        await Ready(service); await service.InstallAsync();
        Assert.Equal(1, backend.Handoffs);
        Assert.Equal(0, lifecycle.Exits);
        Assert.DoesNotContain("resume", lifecycle.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotCallBackend()
    {
        var backend = new Backend();
        using var service = new UpdateService(backend, new Lifecycle());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await service.CheckAsync(cancel.Token);
        Assert.Equal(0, backend.Checks); Assert.Equal(UpdatePhase.Cancelled, service.State.Phase);
    }

    [Fact]
    public async Task EventCallbacksRunOutsideStateLockAndExceptionsAreIsolated()
    {
        var backend = new Backend();
        using var service = new UpdateService(backend, new Lifecycle());
        var received = Signal<UpdateState>();
        service.StateChanged += _ => throw new InvalidOperationException("subscriber");
        service.StateChanged += value =>
        {
            // Another thread must be able to read State while this callback runs.
            var read = Task.Run(() => service.State);
            if (!read.Wait(TimeSpan.FromSeconds(1))) received.TrySetException(new TimeoutException("state lock held"));
            else if (value.Phase == UpdatePhase.Available) received.TrySetResult(value);
        };
        await service.CheckAsync();
        var final = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(service.State.Revision, final.Revision);
    }

    private static async Task Ready(UpdateService service) { await service.CheckAsync(); await service.DownloadAsync(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Backend : IUpdateBackend
    {
        public bool IsManaged { get; set; } = true;
        public string CurrentVersion => "1.0.0";
        public int Checks, Downloads, Handoffs;
        public ConcurrentQueue<string> Calls = new();
        public Func<CancellationToken, Task<UpdateRelease?>> Check = _ => Task.FromResult<UpdateRelease?>(new("1.0.1", "notes", 1024));
        public Func<UpdateRelease, IProgress<int>, CancellationToken, Task> Download = (_, _, _) => Task.CompletedTask;
        public Exception? HandoffError;
        public Action? OnHandoff;
        public Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken) { Checks++; Calls.Enqueue("check"); return Check(cancellationToken); }
        public Task DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken cancellationToken)
        { Downloads++; Calls.Enqueue("download"); return Download(release, progress, cancellationToken); }
        public void PrepareRestart(UpdateRelease release) { Handoffs++; Calls.Enqueue("handoff"); OnHandoff?.Invoke(); if (HandoffError is not null) throw HandoffError; }
    }
    private sealed class Lifecycle : IUpdateLifecycle
    {
        public ConcurrentQueue<string> Calls = new();
        public Func<CancellationToken, Task> Prepare = _ => Task.CompletedTask;
        public int Exits;
        public Exception? ExitError;
        public Task PrepareAsync(CancellationToken cancellationToken) { Calls.Enqueue("prepare"); return Prepare(cancellationToken); }
        public Task ResumeAsync() { Calls.Enqueue("resume"); return Task.CompletedTask; }
        public void Exit() { Calls.Enqueue("exit"); Exits++; if (ExitError is not null) throw ExitError; }
    }
}
