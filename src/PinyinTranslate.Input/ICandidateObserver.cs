namespace PinyinTranslate.Input;

public interface ICandidateObserver : IDisposable
{
    Task<bool> Ready { get; }
    bool IsRunning => Ready.IsCompletedSuccessfully && Ready.Result;
    CandidateObserverFailure? Failure => null;
    bool SetEnabled(bool enabled);
    bool IsCurrent(long version);
    void NotifyInputChanged();
    Task<bool> ValidateAsync(CandidateObservation snapshot, CancellationToken cancellationToken = default);
    event Action<long>? Invalidated;
    event Action? FocusInvalidated;
    event Action<CandidateObservation>? Observed;
    event Action<long>? CandidatesCleared;
    event Action<string>? Status;
}
