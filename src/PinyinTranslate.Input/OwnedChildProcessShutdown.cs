namespace PinyinTranslate.Input;

/// <summary>A held, already-created child process, never a PID lookup or process enumeration.</summary>
public interface IOwnedChildProcess : IDisposable
{
    int ProcessId { get; }
    long StartTimeUtcTicks { get; }
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Verifies ownership metadata, terminates once per attempt, and confirms actual exit.</summary>
public sealed class OwnedChildProcessShutdown
{
    private readonly IOwnedChildProcess process;
    private readonly int processId;
    private readonly long startTimeUtcTicks;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim serial = new(1, 1);
    private bool stopped;

    public OwnedChildProcessShutdown(IOwnedChildProcess process, TimeSpan? timeout = null)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        this.timeout = timeout ?? TimeSpan.FromSeconds(4);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        processId = process.ProcessId;
        startTimeUtcTicks = process.StartTimeUtcTicks;
        if (processId <= 0 || startTimeUtcTicks <= 0) throw new ArgumentException("Child process identity is unavailable.", nameof(process));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stopped) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            if (!process.HasExited)
            {
                if (process.ProcessId != processId || process.StartTimeUtcTicks != startTimeUtcTicks)
                    throw new InvalidOperationException("Owned child process identity changed; termination refused.");
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!process.HasExited) throw new InvalidOperationException("Child process exit was not confirmed.");
            stopped = true;
            process.Dispose();
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("候选观察进程未能及时退出，请稍后重试更新。", error);
        }
        finally { serial.Release(); }
    }
}
