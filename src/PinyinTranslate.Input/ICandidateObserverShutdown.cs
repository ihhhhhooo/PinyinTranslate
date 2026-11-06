namespace PinyinTranslate.Input;

/// <summary>Optional observer lifetime contract; completion includes its owned child process exiting.</summary>
public interface ICandidateObserverShutdown
{
    Task Completion { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}
