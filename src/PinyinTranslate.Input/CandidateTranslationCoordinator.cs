using System.Threading.Channels;

namespace PinyinTranslate.Input;

/// <summary>An immutable preview state. CanRequestAccept also permits waiting for translation.</summary>
public sealed record CandidateTranslationState(
    long Generation,
    bool Enabled,
    CandidateObservation? Observation,
    string? Translation,
    bool IsWaiting,
    bool AcceptRequested,
    bool IsAccepting,
    bool CanRequestAccept,
    string? Error)
{
    public string? Candidate => Observation?.Candidate;
    public CandidateBounds? Bounds => Observation?.Bounds;
}

/// <summary>
/// Coordinates read-only candidate previews independently of native input support.
/// Public methods never call translation, validation, input or event handlers inline.
/// A single observer supplies monotonically increasing source versions; rebuild this
/// coordinator if that observer is replaced by a new version stream.
/// </summary>
public sealed class CandidateTranslationCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<CandidateObservation, CancellationToken, Task<string>> _translateAsync;
    private readonly Func<CandidateObservation, CancellationToken, Task<bool>> _validateAsync;
    private readonly Func<CandidateObservation, string, CancellationToken, Task<bool>>? _acceptAsync;
    private readonly Channel<CandidateTranslationState> _notifications = Channel.CreateUnbounded<CandidateTranslationState>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private CandidateTranslationState _state = new(0, false, null, null, false, false, false, false, null);
    private Operation? _current;
    private long _generation;
    private long _sourceFloor = long.MinValue;
    private long _lastObservedSource = long.MinValue;
    private bool _enabled;
    private int _disposed;

    public CandidateTranslationCoordinator(
        Func<CandidateObservation, CancellationToken, Task<string>> translateAsync,
        Func<CandidateObservation, CancellationToken, Task<bool>> validateAsync,
        Func<CandidateObservation, string, CancellationToken, Task<bool>>? acceptAsync = null)
    {
        ArgumentNullException.ThrowIfNull(translateAsync);
        ArgumentNullException.ThrowIfNull(validateAsync);
        _translateAsync = translateAsync;
        _validateAsync = validateAsync;
        _acceptAsync = acceptAsync;
        _ = Task.Run(DrainNotificationsAsync);
    }

    /// <summary>Raised serially on a background thread, outside all coordinator locks.</summary>
    public event Action<CandidateTranslationState>? Changed;
    /// <summary>Includes delegate and Changed-subscriber failures; subscribers cannot kill workers.</summary>
    public event Action<Exception>? Faulted;
    public bool HasAcceptor => _acceptAsync is not null;
    public CandidateTranslationState State { get { lock (_gate) return _state; } }

    /// <summary>Every call begins a new epoch, including disable followed by re-enable.</summary>
    public void SetEnabled(bool enabled)
    {
        Operation? previous;
        lock (_gate)
        {
            if (_disposed != 0) return;
            previous = _current;
            _current = null;
            _enabled = enabled;
            _generation++;
            PublishLocked();
        }
        Cancel(previous);
    }

    /// <summary>
    /// Copies the page before storing it. Older or duplicate source versions are ignored,
    /// including an old A arriving after A-B-A. A fresh malformed observation invalidates.
    /// </summary>
    public bool Observe(CandidateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var copy = observation with { VisiblePage = Array.AsReadOnly(observation.VisiblePage?.ToArray() ?? []) };
        Operation? previous;
        Operation? operation;
        lock (_gate)
        {
            if (_disposed != 0 || !_enabled || copy.Version < _sourceFloor || copy.Version <= _lastObservedSource) return false;
            _lastObservedSource = copy.Version;
            previous = _current;
            _generation++;
            operation = IsUsable(copy) ? new Operation(_generation, copy) : null;
            _current = operation;
            PublishLocked(operation is null ? "候选快照证据不完整。" : null);
        }
        Cancel(previous);
        if (operation is not null) _ = Task.Run(() => RunOperationAsync(operation));
        return operation is not null;
    }

    /// <summary>Call immediately for external focus, caret, selection or content changes.</summary>
    public void Invalidate() => InvalidateCore(null);

    /// <summary>Connect directly to the observer's Invalidated event. A later observation may use this same source version.</summary>
    public void Invalidate(long sourceVersion) => InvalidateCore(sourceVersion);

    private void InvalidateCore(long? sourceVersion)
    {
        Operation? previous;
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (sourceVersion is { } source)
            {
                if (source <= _lastObservedSource || source <= _sourceFloor) return;
                _sourceFloor = source;
            }
            previous = _current;
            _current = null;
            _generation++;
            PublishLocked();
        }
        Cancel(previous);
    }

    /// <summary>
    /// Suitable for a keyboard hook: only queues one acceptance and returns immediately.
    /// True means this Tab belongs to a current operation with an explicit acceptor;
    /// repeated Tabs for that operation are coalesced. No acceptor always means false.
    /// </summary>
    public bool TryRequestAccept()
    {
        lock (_gate)
        {
            if (_disposed != 0 || !_enabled || _acceptAsync is null || _current is not { } operation) return false;
            if (operation.AcceptRequested) return true;
            operation.AcceptRequested = true;
            PublishLocked();
            operation.AcceptRequestedSignal.TrySetResult(true);
            return true;
        }
    }

    private async Task RunOperationAsync(Operation operation)
    {
        var token = operation.Token;
        try
        {
            if (!IsCurrent(operation)) return;
            if (!await AwaitDelegateAsync(_validateAsync(operation.Observation, token), token).ConfigureAwait(false))
            { ClearIfCurrent(operation); return; }
            if (!IsCurrent(operation)) return;

            var translation = await AwaitDelegateAsync(_translateAsync(operation.Observation, token), token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translation)) throw new InvalidDataException("候选翻译返回空文本。");
            if (!IsCurrent(operation)) return;
            if (!await AwaitDelegateAsync(_validateAsync(operation.Observation, token), token).ConfigureAwait(false))
            { ClearIfCurrent(operation); return; }

            lock (_gate)
            {
                if (!IsCurrentLocked(operation)) return;
                operation.Translation = translation;
                operation.IsWaiting = false;
                PublishLocked();
            }

            if (_acceptAsync is null)
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return;
            }
            await operation.AcceptRequestedSignal.Task.WaitAsync(token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrentLocked(operation)) return;
                operation.IsAccepting = true;
                PublishLocked();
            }

            // Preview validation is not sufficient: re-read immediately before acceptance.
            if (!await AwaitDelegateAsync(_validateAsync(operation.Observation, token), token).ConfigureAwait(false))
            { ClearIfCurrent(operation); return; }
            if (!IsCurrent(operation)) return;
            token.ThrowIfCancellationRequested();

            // This final managed check cannot make a cross-process mutation atomic.
            // The acceptor MUST re-check the token and native focus/composition/caret
            // evidence at its actual write boundary, and abort without writing if stale.
            bool accepted = await AwaitDelegateAsync(_acceptAsync(operation.Observation, translation, token), token).ConfigureAwait(false);
            ClearIfCurrent(operation, accepted ? null : "输入适配器未接受当前候选。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            ClearIfCurrent(operation, error.Message);
            ReportFault(error);
        }
        finally
        {
            Cancel(operation);
            await operation.FinishAsync().ConfigureAwait(false);
        }
    }

    private async Task<T> AwaitDelegateAsync<T>(Task<T> task, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(task);
        try { return await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Delegates may ignore cancellation. Detach promptly but still observe a late failure.
            _ = ObserveDetachedAsync(task);
            throw;
        }
    }

    private async Task ObserveDetachedAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportFault(error); }
    }

    private bool IsCurrent(Operation operation) { lock (_gate) return IsCurrentLocked(operation); }
    private bool IsCurrentLocked(Operation operation) => _disposed == 0 && _enabled &&
        ReferenceEquals(_current, operation) && operation.Generation == _generation && !operation.Token.IsCancellationRequested;

    private void ClearIfCurrent(Operation operation, string? error = null)
    {
        lock (_gate)
        {
            if (!IsCurrentLocked(operation)) return;
            _current = null;
            _generation++;
            PublishLocked(error);
        }
        Cancel(operation);
    }

    private void PublishLocked(string? error = null)
    {
        var operation = _current;
        _state = new(_generation, _enabled, operation?.Observation, operation?.Translation,
            operation?.IsWaiting ?? false, operation?.AcceptRequested ?? false,
            operation?.IsAccepting ?? false, operation is not null && _acceptAsync is not null, error);
        _notifications.Writer.TryWrite(_state);
    }

    private async Task DrainNotificationsAsync()
    {
        try
        {
            await foreach (var state in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _disposed) != 0) break;
                var subscribers = Changed;
                if (subscribers is null) continue;
                foreach (Action<CandidateTranslationState> subscriber in subscribers.GetInvocationList())
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    try { subscriber(state); }
                    catch (Exception error) { ReportFault(error); }
                }
            }
        }
        catch (Exception error) { ReportFault(error); }
    }

    private void ReportFault(Exception error)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(() =>
        {
            var subscribers = Faulted;
            if (subscribers is null || Volatile.Read(ref _disposed) != 0) return;
            foreach (Action<Exception> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(error); } catch (Exception) { /* Fault observers cannot fault a worker. */ }
            }
        });
    }

    private void Cancel(Operation? operation)
    {
        if (operation is null) return;
        try { _ = ObserveDetachedAsync(operation.CancelAsync()); }
        catch (Exception error) { ReportFault(error); }
    }

    private static bool IsUsable(CandidateObservation observation) => observation.ForegroundWindow != 0 &&
        observation.FocusProcessId > 0 && !string.IsNullOrWhiteSpace(observation.FocusRuntimeId) &&
        !string.IsNullOrWhiteSpace(observation.Candidate) && observation.VisiblePage.Count > 0 &&
        observation.Candidate == observation.VisiblePage[0] && observation.Bounds.Width > 0 && observation.Bounds.Height > 0;

    public void Dispose()
    {
        Operation? previous;
        lock (_gate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            _enabled = false;
            previous = _current;
            _current = null;
            _generation++;
            _state = new(_generation, false, null, null, false, false, false, false, null);
            Changed = null;
            Faulted = null;
            _notifications.Writer.TryComplete();
        }
        Cancel(previous);
    }

    private sealed class Operation
    {
        private readonly object _cancellationGate = new();
        private readonly CancellationTokenSource _source = new();
        private Task? _cancellationTask;
        private bool _sourceDisposed;
        public Operation(long generation, CandidateObservation observation)
        {
            Generation = generation;
            Observation = observation;
            Token = _source.Token;
        }
        public long Generation { get; }
        public CandidateObservation Observation { get; }
        public CancellationToken Token { get; }
        public TaskCompletionSource<bool> AcceptRequestedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Translation { get; set; }
        public bool IsWaiting { get; set; } = true;
        public bool AcceptRequested { get; set; }
        public bool IsAccepting { get; set; }

        public Task CancelAsync()
        {
            lock (_cancellationGate)
            {
                if (_sourceDisposed) return Task.CompletedTask;
                return _cancellationTask ??= _source.CancelAsync();
            }
        }

        public async Task FinishAsync()
        {
            try { await CancelAsync().ConfigureAwait(false); }
            catch (Exception) { /* The coordinator observes the shared cancellation task. */ }
            finally
            {
                lock (_cancellationGate)
                {
                    if (!_sourceDisposed) { _sourceDisposed = true; _source.Dispose(); }
                }
            }
        }
    }
}
