namespace PinyinTranslate.App;

internal readonly record struct UpdateModeState(bool ImeEnabled, bool SelectionEnabled);

/// <summary>A reversible pause. Delegates supply application work; this class is entirely managed.</summary>
internal sealed class UpdatePreparation
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly Func<UpdateModeState> capture;
    private readonly Func<CancellationToken, Task> pause;
    private readonly Func<UpdateModeState, Task> resume;
    private readonly TimeSpan timeout;
    private UpdateModeState? previous;
    private bool prepared;
    private int active;

    public UpdatePreparation(Func<UpdateModeState> capture, Func<CancellationToken, Task> pause,
        Func<UpdateModeState, Task> resume, TimeSpan? timeout = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.pause = pause ?? throw new ArgumentNullException(nameof(pause));
        this.resume = resume ?? throw new ArgumentNullException(nameof(resume));
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public bool IsActive => Volatile.Read(ref active) != 0;

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared) return;
            previous ??= capture();
            Volatile.Write(ref active, 1);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                // The pause delegate must establish all mutation barriers before
                // its first await; late drain completion does not change modes.
                await pause(deadline.Token).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                prepared = true;
            }
            catch (Exception error)
            {
                try { await ResumeLockedAsync(); }
                catch (Exception resumeError)
                {
                    throw new InvalidOperationException("更新准备未完成，恢复翻译模式失败；可在主界面重新开启。", new AggregateException(error, resumeError));
                }
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                throw new InvalidOperationException("更新准备未完成，程序仍在运行，请稍后重试。", error);
            }
        }
        finally { serial.Release(); }
    }

    public async Task ResumeAsync()
    {
        await serial.WaitAsync();
        try { await ResumeLockedAsync(); }
        finally { serial.Release(); }
    }

    private async Task ResumeLockedAsync()
    {
        if (previous is not { } modes) return;
        try { await resume(modes); }
        finally
        {
            // Even a failed mode restore must leave the existing app usable.
            // Native ownership remains tracked separately for the next attempt.
            prepared = false;
            previous = null;
            Volatile.Write(ref active, 0);
        }
    }
}
