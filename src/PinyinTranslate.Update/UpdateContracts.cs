namespace PinyinTranslate.Update;

public enum UpdatePhase
{
    Idle, Checking, UpToDate, Available, Downloading, ReadyToInstall,
    PreparingRestart, RestartScheduled, MigrationRequired, Failed, Cancelled
}

public sealed record UpdateRelease(string Version, string Notes = "", long DownloadBytes = 0)
{
    // A release is bound to its checked SDK session; callers cannot manufacture a
    // downloadable or installable SDK ticket by copying displayed metadata.
    internal object? BackendToken { get; init; }
}

public sealed record UpdateState(long Revision, UpdatePhase Phase, string CurrentVersion,
    UpdateRelease? Release, int Progress, string Message)
{
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.PreparingRestart;
    public bool CanCheck => !IsBusy && Phase is not (UpdatePhase.RestartScheduled or UpdatePhase.MigrationRequired);
    public bool CanDownload => Release is not null && Phase is (UpdatePhase.Available or UpdatePhase.Cancelled or UpdatePhase.Failed);
    public bool CanInstall => Release is not null && Phase == UpdatePhase.ReadyToInstall;
    public bool CanCancel => Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
}

public interface IUpdateBackend
{
    bool IsManaged { get; }
    string CurrentVersion { get; }
    Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken cancellationToken);
    /// <summary>Start the official updater waiting for this process; never exit this process.</summary>
    void PrepareRestart(UpdateRelease release);
}

public interface IUpdateLifecycle
{
    /// <summary>Pause reversibly and wait for helpers to finish; do not permanently dispose the app.</summary>
    Task PrepareAsync(CancellationToken cancellationToken);
    Task ResumeAsync();
    void Exit();
}
